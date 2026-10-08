package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.content.Context;
import android.graphics.ImageFormat;
import android.graphics.SurfaceTexture;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Size;
import android.view.Gravity;
import android.view.Surface;
import android.view.TextureView;
import android.widget.FrameLayout;
import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.ReaderException;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.Collections;
import java.util.EnumMap;
import java.util.Map;

/**
 * Reads a QR code with the back camera: a live picture in the given frame, and each camera image is searched
 * for a code off the main thread. Nothing is stored or sent anywhere; the text of the first code is handed back.
 */
final class Scanner {
    interface Found { void text(String value); }
    interface Failed { void because(String message); }

    private final Activity activity;
    private final FrameLayout frame;
    private final Found found;
    private final Failed failed;
    private final TextureView picture;
    private HandlerThread thread;
    private Handler worker;
    private CameraDevice camera;
    private CameraCaptureSession session;
    private ImageReader reader;
    private Size size = new Size(1280, 960);
    private volatile boolean running, done;
    private long lastLook;

    Scanner(Activity activity, FrameLayout frame, Found found, Failed failed) {
        this.activity = activity; this.frame = frame; this.found = found; this.failed = failed;
        picture = new TextureView(activity);
    }

    void start() {
        if (running) return;
        running = true; done = false;
        thread = new HandlerThread("scanner");
        thread.start();
        worker = new Handler(thread.getLooper());
        if (picture.getParent() == null) frame.addView(picture, new FrameLayout.LayoutParams(-1, -1, Gravity.CENTER));
        if (picture.isAvailable()) open();
        else picture.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            @Override public void onSurfaceTextureAvailable(SurfaceTexture texture, int width, int height) { open(); }
            @Override public void onSurfaceTextureSizeChanged(SurfaceTexture texture, int width, int height) { }
            @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture texture) { return true; }
            @Override public void onSurfaceTextureUpdated(SurfaceTexture texture) { }
        });
    }

    void stop() {
        running = false;
        try { if (session != null) session.close(); } catch (Exception ignored) { }
        try { if (camera != null) camera.close(); } catch (Exception ignored) { }
        try { if (reader != null) reader.close(); } catch (Exception ignored) { }
        session = null; camera = null; reader = null;
        if (thread != null) { thread.quitSafely(); thread = null; }
    }

    private void fail(String message) { activity.runOnUiThread(() -> { if (running) { stop(); failed.because(message); } }); }

    /** An image size the camera offers that is close to 1280 x 960: enough for a code on a monitor, quick to search. */
    private static Size choose(Size[] offered) {
        Size best = null;
        long bestScore = Long.MAX_VALUE;
        for (Size s : offered == null ? new Size[0] : offered) {
            long score = Math.abs((long) s.getWidth() * s.getHeight() - 1280L * 960L) + (s.getWidth() * 3 == s.getHeight() * 4 ? 0 : 200000);
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best == null ? new Size(1280, 960) : best;
    }

    @SuppressWarnings("MissingPermission")      // asked for by MainActivity before start()
    private void open() {
        if (!running) return;
        try {
            CameraManager manager = (CameraManager) activity.getSystemService(Context.CAMERA_SERVICE);
            String chosen = null;
            for (String id : manager.getCameraIdList()) {
                Integer facing = manager.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING);
                if (chosen == null) chosen = id;
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) { chosen = id; break; }
            }
            if (chosen == null) { fail("这台手机没有可用的相机"); return; }
            StreamConfigurationMap map = manager.getCameraCharacteristics(chosen).get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
            size = choose(map == null ? null : map.getOutputSizes(ImageFormat.YUV_420_888));
            // The camera's image is wider than tall; on an upright phone it is shown turned, so the frame crops a tall picture.
            int side = frame.getWidth();
            if (side > 0) {
                FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(side, Math.round(side * (float) size.getWidth() / size.getHeight()), Gravity.CENTER);
                picture.setLayoutParams(params);
            }
            manager.openCamera(chosen, new CameraDevice.StateCallback() {
                @Override public void onOpened(CameraDevice device) { camera = device; if (running) preview(); else device.close(); }
                @Override public void onDisconnected(CameraDevice device) { device.close(); }
                @Override public void onError(CameraDevice device, int error) { device.close(); fail("相机打不开（" + error + "）"); }
            }, worker);
        } catch (CameraAccessException | SecurityException | IllegalArgumentException error) { fail("相机打不开"); }
    }

    private void preview() {
        try {
            SurfaceTexture texture = picture.getSurfaceTexture();
            if (texture == null) { fail("相机画面没有准备好"); return; }
            texture.setDefaultBufferSize(size.getWidth(), size.getHeight());
            final Surface shown = new Surface(texture);
            reader = ImageReader.newInstance(size.getWidth(), size.getHeight(), ImageFormat.YUV_420_888, 2);
            reader.setOnImageAvailableListener(this::look, worker);
            final CaptureRequest.Builder request = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
            request.addTarget(shown);
            request.addTarget(reader.getSurface());
            request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
            camera.createCaptureSession(Arrays.asList(shown, reader.getSurface()), new CameraCaptureSession.StateCallback() {
                @Override public void onConfigured(CameraCaptureSession made) {
                    session = made;
                    try { if (running) made.setRepeatingRequest(request.build(), null, worker); } catch (CameraAccessException | IllegalStateException error) { fail("相机画面没有开始"); }
                }
                @Override public void onConfigureFailed(CameraCaptureSession made) { fail("相机画面没有开始"); }
            }, worker);
        } catch (CameraAccessException | IllegalStateException | IllegalArgumentException error) { fail("相机画面没有开始"); }
    }

    /** Searches one camera image for a code, at most about seven times a second. */
    private void look(ImageReader source) {
        Image image = null;
        try {
            image = source.acquireLatestImage();
            if (image == null || done || !running) return;
            long now = System.currentTimeMillis();
            if (now - lastLook < 140) return;
            lastLook = now;
            int width = image.getWidth(), height = image.getHeight();
            Image.Plane plane = image.getPlanes()[0];
            ByteBuffer buffer = plane.getBuffer();
            int stride = plane.getRowStride();
            byte[] grey = new byte[width * height];
            if (stride == width) buffer.get(grey, 0, Math.min(grey.length, buffer.remaining()));
            else for (int row = 0; row < height && buffer.remaining() >= width; row++) {
                buffer.position(row * stride);
                buffer.get(grey, row * width, width);
            }
            String text = decode(grey, width, height);
            if (text != null && !done) {
                done = true;
                activity.runOnUiThread(() -> { if (running) { stop(); found.text(text); } });
            }
        } catch (Exception ignored) {
            // an image that could not be read: the next one is tried
        } finally { if (image != null) image.close(); }
    }

    /** The text of a QR code in a grey image, or null. Separate from the camera so it can be tested without one. */
    static String decode(byte[] grey, int width, int height) {
        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE));
        hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
        try {
            return new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(new PlanarYUVLuminanceSource(grey, width, height, 0, 0, width, height, false))), hints).getText();
        } catch (ReaderException none) { return null; }
    }
}
