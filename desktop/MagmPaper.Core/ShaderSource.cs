using System.Text;
using System.Text.RegularExpressions;

namespace MagmPaper.Core;

public static class ShaderSource
{
    // Some GLSL Sandbox examples place #extension after precision/uniforms.
    // Desktop ESSL requires it in the preamble. Keep files and diagnostic line
    // numbers intact; do not move directives controlled by user-defined macros.
    public static string Prepare(string source)
    {
        source = source.TrimStart('\uFEFF');
        var lines = source.Split('\n');
        var header = new StringBuilder();
        var guards = new List<List<string>>();
        int versionLine = -1; string? version = null, versionComments = null;
        bool comment = false, continued = false;
        for (int index = 0; index < lines.Length; index++)
        {
            var visible = new StringBuilder();
            var commentsOnly = lines[index].ToCharArray();
            for (int pos = 0; pos < lines[index].Length; pos++)
            {
                char current = lines[index][pos], next = pos + 1 < lines[index].Length ? lines[index][pos + 1] : '\0';
                if (comment) { if (current == '*' && next == '/') { comment = false; pos++; visible.Append(' '); } continue; }
                if (current == '/' && next == '/') break;
                if (current == '/' && next == '*') { comment = true; pos++; visible.Append(' '); continue; }
                visible.Append(current);
                commentsOnly[pos] = ' ';
            }
            string text = visible.ToString().Trim(); bool previousContinuation = continued;
            continued = text.EndsWith('\\');
            if (previousContinuation || continued) continue;
            var directive = Regex.Match(text, @"^#\s*(\w+)\b(.*)$");
            if (!directive.Success) continue;
            string name = directive.Groups[1].Value;
            if (name is "if" or "ifdef" or "ifndef") guards.Add([text]);
            else if (name is "else" or "elif") { if (guards.Count > 0) guards[^1].Add(text); }
            else if (name == "endif") { if (guards.Count > 0) guards.RemoveAt(guards.Count - 1); }
            else if (name == "version" && guards.Count == 0) { version = text; versionLine = index; versionComments = new string(commentsOnly); }
            else if (name == "extension" && guards.All(group => group.All(IsBuiltinGuard)))
            {
                foreach (var group in guards) foreach (var guard in group) header.Append(guard).Append('\n');
                header.Append(text).Append('\n');
                for (int count = 0; count < guards.Count; count++) header.Append("#endif\n");
                lines[index] = new string(commentsOnly);
            }
        }
        if (header.Length == 0) return source;
        if (versionLine >= 0) { lines[versionLine] = versionComments!; header.Insert(0, version + "\n"); }
        return header.Append("#line 1\n").AppendJoin('\n', lines).ToString();
    }
    private static bool IsBuiltinGuard(string guard) => Regex.Matches(guard, @"[A-Za-z_]\w*")
        .All(match => match.Value is "if" or "ifdef" or "ifndef" or "elif" or "else" or "defined" || match.Value.StartsWith("GL_", StringComparison.Ordinal));
}
