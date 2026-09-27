using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GOHShaderModdingSupportLauncher
{
    internal static class FileManager
    {
        private const long MaxSettingsBytes = 32 * 1024;
        internal const int LanguageSettingIndex = 13;
        private const int MaxSettingsLength = 17;

        public static bool IsFileError(Exception ex) => ex
                                                        is IOException
                                                        or UnauthorizedAccessException
                                                        or System.Security.SecurityException
                                                        or ArgumentException
                                                        ;

        public static string SettingsPath => Path.Combine(
                                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                                                , "GOHSMSLauncher"
                                                , "settings.conf"
                                                );

        #region Main Functions
        public static string[] ReadSettings(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (stream.Length > MaxSettingsBytes)
            {
                throw new FormatException("The launcher settings file is too large.");
            }

            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);

            string[] lines;
            try
            {
                var parsed = new List<string>(MaxSettingsLength);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 4096 || line.Any(char.IsControl))
                    {
                        throw new FormatException("The launcher settings file contains an invalid field.");
                    }

                    parsed.Add(line);

                    if (parsed.Count > MaxSettingsLength)
                    {
                        throw new FormatException("The launcher settings file contains too many lines.");
                    }
                }

                lines = parsed.ToArray();
            }
            catch (DecoderFallbackException ex)
            {
                throw new FormatException("The launcher settings file has invalid text encoding.", ex);
            }

            if (lines.Length is not (13 or 14 or 15 or 16 or 17))
            {
                throw new FormatException("The launcher settings file must contain 13, 14, 15, 16, or 17 settings.");
            }

            if (
                !Enum.TryParse<MainWindow.LauncherVars.LaunchMethod>(lines[0], out var method)
                || Enum.IsDefined(method) == false
                || string.Equals(lines[0], method.ToString(), StringComparison.Ordinal) == false
                )
            {
                throw new FormatException("Invalid launch method in settings.");
            }

            for (int i = 1; i <= 10; i++)
            {
                if (lines[i] != bool.TrueString && lines[i] != bool.FalseString)
                {
                    throw new FormatException($"Invalid setting at line {i + 1}.");
                }
            }

            if (IsValidCacheHash(lines[11]) == false || IsValidShaderHash(lines[12]) == false)
                throw new FormatException("Invalid shader cache hash in settings.");

            if ((lines.Length is 14 or 16 or 17) && lines[LanguageSettingIndex] is not ("en-US" or "zh-CN"))
                throw new FormatException("Invalid language in settings.");

            if ((lines.Length is 15 or 16) && (IsStandardAbsolutePath(lines[^2]) == false || IsStandardAbsolutePath(lines[^1]) == false))
                throw new FormatException("Invalid cached path in settings.");

            if (lines.Length == 17 &&
                ((lines[14].Length > 0 && !IsStandardAbsolutePath(lines[14])) ||
                 (lines[15].Length > 0 && !IsStandardAbsolutePath(lines[15])) ||
                 (lines[16].Length > 0 && !IsStandardAbsolutePath(lines[16]))))
                throw new FormatException("Invalid account or cached path in settings.");

            return lines;
        }

        public static void WriteSettings(string path, IEnumerable<string> lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temporary, lines);
                File.Move(temporary, path, true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (IsFileError(ex)) { AppDiagnostics.Log("Settings temp file cleanup failed.", ex); }
            }
        }

        public static string? FindOptionsFile(string profileRoot) => FindOptionsFiles(profileRoot).FirstOrDefault();

        public static string[] FindOptionsFiles(string profileRoot)
        {
            if (!Path.IsPathFullyQualified(profileRoot)) return [];
            var profiles = Path.Combine(profileRoot, "profiles");
            if (!Directory.Exists(profiles)) return [];
            return Directory.EnumerateDirectories(profiles)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "options.set"))
                .Where(File.Exists).ToArray();
        }

        public static bool IsGameDirectory(string path)
        {
            if (IsStandardAbsolutePath(path) == false) return false;

            var directory = new DirectoryInfo(path);

            if (
                directory.Name.Equals("x64", StringComparison.OrdinalIgnoreCase) == false
                || string.Equals(directory.Parent?.Name, "binaries", StringComparison.OrdinalIgnoreCase) == false
                || directory.Parent?.Parent == null
                 ) return false;

            return
                File.Exists(Path.Combine(path, "call_to_arms.exe"))
                && Directory.Exists(Path.Combine(directory.Parent.Parent.FullName, "resource"))
                ;
        }

        public static IEnumerable<string> ReadLibraryPaths(string vdfPath)
        {
            foreach (string line in File.ReadLines(vdfPath))
            {
                var parts = line.Split('"');
                if (
                    parts.Length >= 5
                    && parts[1].Equals("path", StringComparison.OrdinalIgnoreCase)
                    ) yield return parts[3].Replace("\\\\", "\\");
            }
        }

        public static IEnumerable<string> ReadLoadedModNames(TextReader reader)
        {
            string? line;
            while (
                (line = reader.ReadLine()) != null
                && line.Contains("{mods") == false
                ) { }

            if (line == null) yield break;

            while (
                (line = reader.ReadLine()) != null
                && line.Contains('}') == false
                )
            {
                int start = line.IndexOf('"');
                int end = line.LastIndexOf('"');

                if (start < 0 || end <= start) continue;

                string name = line.Substring(start + 1, end - start - 1)
                                    .Split(':')[0]
                                    ;

                if (name.Length > 0) yield return name;
            }
        }

        internal static void WriteTextAtomically(string path, string text, bool overwrite)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (overwrite && File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (IsFileError(ex)) { AppDiagnostics.Log("Temporary file cleanup failed.", ex); }
            }
        }

        internal static void WriteLoadedMods(string path, IReadOnlyList<Mod> mods)
        {
            // Keep all unrelated settings, including sections following {mods}.
            var text = File.ReadAllText(path);
            int start = -1, end = -1, sectionDepth = -1, depth = 0, rootEnd = -1;
            bool quoted = false, escaped = false, comment = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (comment) { if (c == '\n') comment = false; continue; }
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') { quoted = true; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { comment = true; continue; }
                if (c == '{')
                {
                    depth++;
                    int keyStart = i + 1;
                    while (keyStart < text.Length && char.IsWhiteSpace(text[keyStart])) keyStart++;
                    int keyEnd = keyStart;
                    while (keyEnd < text.Length && !char.IsWhiteSpace(text[keyEnd]) && text[keyEnd] is not ('{' or '}')) keyEnd++;
                    if (text.AsSpan(keyStart, keyEnd - keyStart).SequenceEqual("mods"))
                    {
                        if (start >= 0) throw new FormatException(PresetText.Get("P_InvalidOptions"));
                        start = i;
                        sectionDepth = depth;
                    }
                }
                else if (c == '}')
                {
                    if (depth == sectionDepth && end < 0) end = i + 1;
                    if (--depth < 0) throw new FormatException(PresetText.Get("P_InvalidOptions"));
                    if (depth == 0) rootEnd = i;
                }
            }
            if (depth != 0 || quoted || rootEnd < 0 || (start >= 0 && end < 0))
                throw new FormatException(PresetText.Get("P_InvalidOptions"));
            var section = new StringBuilder("{mods\r\n");
            foreach (var mod in mods)
            {
                if (string.IsNullOrWhiteSpace(mod.folderName) || mod.folderName.Any(c => char.IsControl(c) || c is '"' or ':' or '\\'))
                    throw new FormatException(PresetText.Get("P_InvalidEntries"));
                section.Append("\t\t\"").Append(mod.folderName).Append(":0\"\r\n");
            }
            section.Append("\t}");
            var modified = start >= 0 ? text[..start] + section + text[end..]
                : text[..rootEnd] + "\t" + section + "\r\n" + text[rootEnd..];
            WriteTextAtomically(path, modified, overwrite: true);
        }

        public static string NormalizeCacheHash(string? hash) => IsValidCacheHash(hash) ? hash! : "-1";
        public static string NormalizeShaderHash(string? hash) => IsValidShaderHash(hash) ? hash! : "0";

        #endregion

        #region Tools
        private static bool IsValidCacheHash(string? hash) => hash == "-1" || IsBase64Hash(hash, 64);
        private static bool IsValidShaderHash(string? hash) => hash == "0" || IsBase64Hash(hash, 32);

        private static bool IsBase64Hash(string? text, int bytes)
        {
            if (
                text == null
                || text.Length != ((bytes + 2) / 3) * 4
                ) return false;

            Span<byte> buffer = stackalloc byte[64];
            return Convert.TryFromBase64String(text, buffer, out int count)
                    && count == bytes
                    && Convert.ToBase64String(buffer[..count]) == text
                    ;
        }

        private static bool IsStandardAbsolutePath(string path)
        {
            if (
                path.Length == 0
                || path.Length > 4096
                || !Path.IsPathFullyQualified(path)
                ) return false;

            if (
                path.StartsWith(@"\\?\", StringComparison.Ordinal)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                ) return false;

            try
            {
                string standard = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                return string.Equals(standard, Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (IsFileError(ex)) { return false; }
        }
        #endregion
    }
}
