import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;
import java.awt.Color;
import java.awt.Graphics2D;
import java.awt.RenderingHints;
import java.awt.image.BufferedImage;
import java.io.File;
import java.util.Collections;
import java.util.EnumMap;
import java.util.Map;
import java.util.Random;
import javax.imageio.ImageIO;

/**
 * The phone's reader, without a phone: puts the code the Windows program draws into a camera-sized grey image
 * (smaller, turned, on a noisy background) and reads it the way Scanner.decode does.
 *
 *   java -cp zxing-core.jar tests/QrDecodeCheck.java <code.png> <expected text>
 */
public class QrDecodeCheck {
    public static void main(String[] args) throws Exception {
        BufferedImage code = ImageIO.read(new File(args[0]));
        int width = 1280, height = 960, passed = 0;
        double[][] cases = { {0, 0.9}, {90, 0.7}, {180, 0.5}, {270, 0.6}, {12, 0.55}, {-8, 0.8} };      // degrees turned, share of the image height
        for (double[] one : cases) {
            BufferedImage scene = new BufferedImage(width, height, BufferedImage.TYPE_BYTE_GRAY);
            Graphics2D g = scene.createGraphics();
            g.setColor(new Color(70, 70, 70)); g.fillRect(0, 0, width, height);
            g.setRenderingHint(RenderingHints.KEY_INTERPOLATION, RenderingHints.VALUE_INTERPOLATION_BILINEAR);
            double side = height * one[1], scale = side / code.getWidth();
            g.translate(width / 2.0, height / 2.0); g.rotate(Math.toRadians(one[0])); g.scale(scale, scale);
            g.drawImage(code, -code.getWidth() / 2, -code.getHeight() / 2, null);
            g.dispose();
            byte[] grey = new byte[width * height];
            Random noise = new Random(7);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                grey[y * width + x] = (byte) Math.max(0, Math.min(255, (scene.getRaster().getSample(x, y, 0)) + noise.nextInt(25) - 12));
            Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
            hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE));
            hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
            String text;
            try { text = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(new PlanarYUVLuminanceSource(grey, width, height, 0, 0, width, height, false))), hints).getText(); }
            catch (Exception none) { text = null; }
            boolean ok = args[1].equals(text);
            if (ok) passed++;
            System.out.println("turned " + (int) one[0] + ", size " + one[1] + ": " + (ok ? "read" : "NOT READ"));
        }
        System.out.println(passed + " of " + cases.length);
        if (passed != cases.length) System.exit(1);
    }
}
