using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RemoteCli;

/// One conversation is listed once, however many files its tool keeps of it.
public static class SessionCopiesCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static TerminalAgent.SessionInfo Saved(string id, string tool, string same, long updated, bool live = false) {
        return new TerminalAgent.SessionInfo { Id = id, Tool = tool, Dir = "demo", Title = same, Same = same, Updated = updated, Live = live };
    }
    static string Write(string folder, string name, params string[] lines) {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, String.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }
    public static int Main() {
        var listed = TerminalAgent.Single(new[] {
            Saved("a", "claude", "重构列表", 10), Saved("b", "claude", "重构列表", 30), Saved("c", "claude", "重构列表", 20),
            Saved("d", "codex", "重构列表", 5), Saved("e", "claude", "", 1), Saved("f", "claude", "", 2),
            Saved("g", "codex", "部署", 40), Saved("h", "codex", "部署", 50, true), Saved("i", "codex", "部署", 60, true) });
        Require(String.Join(",", listed.Select(s => s.Id).OrderBy(s => s)) == "b,d,e,f,h,i", "copies: " + String.Join(",", listed.Select(s => s.Id)));

        string folder = Path.Combine(Path.GetTempPath(), "rcli-copies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            string same;
            // A named conversation and its branch carry the name; what each was asked last does not matter.
            string named = Write(folder, "named.jsonl", "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"第一句\"}}", "{\"type\":\"ai-title\",\"aiTitle\":\"聚合界面重构\"}", "{\"type\":\"last-prompt\",\"lastPrompt\":\"继续\"}");
            Require(TerminalAgent.ClaudeTitleOf(named, out same) == "聚合界面重构" && same == "聚合界面重构", "named: " + same);
            // Without a name the title is the last request, which unrelated conversations share; what they began with tells them apart.
            string one = Write(folder, "one.jsonl", "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"检查中转\"}}", "{\"type\":\"last-prompt\",\"lastPrompt\":\"继续\"}");
            string two = Write(folder, "two.jsonl", "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"写测试\"}}", "{\"type\":\"last-prompt\",\"lastPrompt\":\"继续\"}");
            string again = Write(folder, "again.jsonl", "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"content\":\"检查中转\"}", "{\"type\":\"last-prompt\",\"lastPrompt\":\"检查中转\"}");
            string sameOne, sameTwo, sameAgain;
            string titleOne = TerminalAgent.ClaudeTitleOf(one, out sameOne), titleTwo = TerminalAgent.ClaudeTitleOf(two, out sameTwo);
            Require(titleOne == "继续" && titleTwo == "继续", "titles by last request");
            Require(sameOne != sameTwo && sameOne.Length > 0, "unrelated conversations must stay apart: " + sameOne + " / " + sameTwo);
            TerminalAgent.ClaudeTitleOf(again, out sameAgain);
            Require(sameAgain == sameOne, "a second run of one request is a copy: " + sameAgain);
            Require(TerminalAgent.ClaudeTitleOf(named) == "聚合界面重构", "title alone");
        } finally { Directory.Delete(folder, true); }
        Console.WriteLine("Session copies: one entry per conversation, open copies kept, unrelated conversations apart");
        return 0;
    }
}
