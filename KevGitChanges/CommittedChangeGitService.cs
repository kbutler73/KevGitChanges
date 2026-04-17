using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KevGitChanges
{
    internal enum CommittedChangeKind
    {
        Added,
        Modified,
        Deleted
    }

    internal struct CommittedLineChange
    {
        public int LineNumber;
        public CommittedChangeKind Kind;
    }

    internal static class CommittedChangeGitService
    {
        private const string SelectedRemote = "origin";
        private static readonly Regex HunkRegex = new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

        [DataContract]
        private sealed class RepoSettings
        {
            [DataMember(Name = "baseBranch", EmitDefaultValue = false)]
            public string BaseBranch { get; set; }
        }

        public static IReadOnlyList<CommittedLineChange> GetCommittedLineChanges(string filePath, int lineCount)
        {
            var changes = new Dictionary<int, CommittedChangeKind>();
            if (string.IsNullOrWhiteSpace(filePath) || lineCount < 0)
            {
                return Array.Empty<CommittedLineChange>();
            }

            var directory = Path.GetDirectoryName(filePath);
            var repoRoot = ResolveRepoRoot(directory);
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                return Array.Empty<CommittedLineChange>();
            }

            var relativePath = GetRelativePath(repoRoot, filePath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return Array.Empty<CommittedLineChange>();
            }

            relativePath = relativePath.Replace('\\', '/');
            if (IsGitIgnored(repoRoot, relativePath))
            {
                return Array.Empty<CommittedLineChange>();
            }

            var baseRef = GetBaseReference(repoRoot);
            if (string.IsNullOrWhiteSpace(baseRef))
            {
                return Array.Empty<CommittedLineChange>();
            }

            var diff = RunGit(repoRoot, $"diff --no-ext-diff --unified=0 --no-color \"{baseRef}\" -- \"{relativePath}\"");
            if (string.IsNullOrWhiteSpace(diff) || IsGitError(diff))
            {
                return Array.Empty<CommittedLineChange>();
            }

            using (var reader = new StringReader(diff))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var match = HunkRegex.Match(line);
                    if (!match.Success) continue;

                    var oldCount = ParseCount(match.Groups[2].Value);
                    var newStart = ParseCount(match.Groups[3].Value);
                    var newCount = ParseCount(match.Groups[4].Value);

                    if (newCount == 0)
                    {
                        if (lineCount <= 0) continue;
                        var deletedLine = Math.Max(1, Math.Min(lineCount, newStart));
                        AddOrUpdate(changes, deletedLine, oldCount == 0 ? CommittedChangeKind.Added : CommittedChangeKind.Deleted);
                        continue;
                    }

                    var kind = oldCount == 0 ? CommittedChangeKind.Added : CommittedChangeKind.Modified;
                    for (var lineNumber = newStart; lineNumber < newStart + newCount; lineNumber++)
                    {
                        if (lineNumber < 1 || lineNumber > lineCount) continue;
                        AddOrUpdate(changes, lineNumber, kind);
                    }
                }
            }

            var results = new List<CommittedLineChange>(changes.Count);
            foreach (var entry in changes)
            {
                results.Add(new CommittedLineChange
                {
                    LineNumber = entry.Key,
                    Kind = entry.Value
                });
            }

            results.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));
            return results;
        }

        private static void AddOrUpdate(IDictionary<int, CommittedChangeKind> changes, int lineNumber, CommittedChangeKind kind)
        {
            if (!changes.TryGetValue(lineNumber, out var existing))
            {
                changes[lineNumber] = kind;
                return;
            }

            if (existing == CommittedChangeKind.Added || existing == kind)
            {
                return;
            }

            if (kind == CommittedChangeKind.Added || existing == CommittedChangeKind.Deleted)
            {
                changes[lineNumber] = kind;
            }
        }

        private static int ParseCount(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 1;
            return int.TryParse(value, out var count) ? count : 1;
        }

        private static string GetBaseReference(string repoRoot)
        {
            var selected = LoadSelectedBaseBranch(repoRoot);
            if (!string.IsNullOrWhiteSpace(selected) && RefExists(repoRoot, selected))
            {
                return selected.Trim();
            }

            var candidates = new[]
            {
                SelectedRemote + "/main",
                SelectedRemote + "/master",
                SelectedRemote + "/develop",
                SelectedRemote + "/dev",
                "main",
                "master",
                "develop",
                "dev"
            };

            foreach (var candidate in candidates)
            {
                if (RefExists(repoRoot, candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool RefExists(string repoRoot, string refName)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(refName)) return false;
            var result = RunGit(repoRoot, $"rev-parse --verify \"{refName}\"", out var exitCode);
            return exitCode == 0 && !IsGitError(result);
        }

        private static bool IsGitIgnored(string repoRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(relativePath)) return false;
            RunGit(repoRoot, $"check-ignore -q -- \"{relativePath}\"", out var exitCode);
            return exitCode == 0;
        }

        private static string LoadSelectedBaseBranch(string repoRoot)
        {
            try
            {
                var file = GetRepoSettingsPath(repoRoot);
                if (!File.Exists(file)) return null;

                using (var stream = File.OpenRead(file))
                {
                    var serializer = new DataContractJsonSerializer(typeof(RepoSettings));
                    var settings = serializer.ReadObject(stream) as RepoSettings;
                    return string.IsNullOrWhiteSpace(settings?.BaseBranch) ? null : settings.BaseBranch.Trim();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GetRepoSettingsPath(string repoRoot)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "KevGitChanges");
            var normalizedRoot = NormalizeRepoSettingsKey(repoRoot);
            var key = normalizedRoot.ToLowerInvariant();
            var hash = ComputeSha1(key);
            var repoName = Path.GetFileName(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            repoName = SanitizeFileName(string.IsNullOrWhiteSpace(repoName) ? "repo" : repoName);
            return Path.Combine(dir, repoName + "." + hash + ".settings.json");
        }

        private static string NormalizeRepoSettingsKey(string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(repoRoot)) return string.Empty;
            return ResolveRepoRoot(repoRoot) ?? repoRoot.Trim();
        }

        private static string ComputeSha1(string input)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }

                return sb.ToString();
            }
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "repo";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                var replace = false;
                for (var i = 0; i < invalid.Length; i++)
                {
                    if (ch == invalid[i])
                    {
                        replace = true;
                        break;
                    }
                }

                sb.Append(replace ? '_' : ch);
            }

            return sb.ToString();
        }

        private static string ResolveRepoRoot(string workDir)
        {
            if (string.IsNullOrWhiteSpace(workDir)) return null;
            try
            {
                var dir = new DirectoryInfo(workDir);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string GetRelativePath(string rootPath, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(fullPath)) return null;

            var rootUri = new Uri(AppendDirectorySeparator(rootPath));
            var pathUri = new Uri(fullPath);
            if (!rootUri.IsBaseOf(pathUri)) return null;

            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', '\\');
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static bool IsGitError(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return false;
            var trimmed = payload.TrimStart();
            return trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
        }

        private static string RunGit(string workingDirectory, string arguments)
        {
            return RunGit(workingDirectory, arguments, out _);
        }

        private static string RunGit(string workingDirectory, string arguments, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo("git", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                };

                using (var process = Process.Start(psi))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                    if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                    {
                        return error;
                    }

                    return output;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
