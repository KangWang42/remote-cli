"""Source regression checks for the multi-computer aggregate screen."""
from pathlib import Path


root = Path(__file__).resolve().parents[1]
source = (root / "android/src/io/github/kangwang42/remotecli/MainActivity.java").read_text(encoding="utf-8")
assert 'button("聚合查看所有项目和对话", 1, this::aggregate)' in source
assert 'new URL(url + "/api/terminal")' in source
assert 'private void aggregateView(ArrayList<ComputerSnapshot> snapshots)' in source
assert 'press(projectCard, () -> open(url, "", target))' in source
assert 'if ("aggregate".equals(screen)) { home(""); return; }' in source
print("android aggregate checks passed")
