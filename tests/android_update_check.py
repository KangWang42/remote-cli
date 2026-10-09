"""Guard the PackageInstaller stream lifetime that caused Android update failures."""
from pathlib import Path


source = (Path(__file__).resolve().parents[1] / "android/src/io/github/kangwang42/remotecli/GithubUpdater.java").read_text(encoding="utf-8")
write = source.index('try (InputStream in = new FileInputStream(apk); OutputStream out = session.openWrite("base.apk"')
commit = source.index("session.commit", write)
close = source.index("\n            }\n            Intent status", write)
assert write < close < commit, "PackageInstaller.commit must run after the APK output stream closes"
assert "if (!committed) try { session.abandon();" in source
assert 'android:versionName="0.5.5"' in (Path(__file__).resolve().parents[1] / "android/AndroidManifest.xml").read_text(encoding="utf-8")
print("android updater checks passed")
