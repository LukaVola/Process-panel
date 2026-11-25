using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace Procesi
{
    internal sealed class RuleStore
    {
        private sealed class Desired
        {
            internal readonly string Value;
            internal Desired(string value) { Value = value; }
        }

        private sealed class Attempt
        {
            internal Desired Rule;
            internal string Error;
            internal DateTime NextTry;
        }

        private static readonly StringComparer PathsComparer = StringComparer.OrdinalIgnoreCase;
        private readonly string directory;
        private readonly string file;
        private readonly object stateGate = new object();
        private readonly object writeGate = new object();

        // Published dictionaries are immutable. A Desired object identifies one revision of one setting.
        private Dictionary<string, Dictionary<string, Desired>> rules =
            new Dictionary<string, Dictionary<string, Desired>>(PathsComparer);
        private Dictionary<Tuple<int, long, string>, Attempt> attempts =
            new Dictionary<Tuple<int, long, string>, Attempt>();
        private readonly Dictionary<Tuple<int, long, string>, Desired> overrides =
            new Dictionary<Tuple<int, long, string>, Desired>();
        private int reconciling;
        private string storageError = "";
        private string runtimeError = "";

        public RuleStore()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcesniPanel"))
        {
        }

        internal RuleStore(string dataDirectory)
        {
            directory = dataDirectory;
            file = Path.Combine(directory, "rules.xml");
            try
            {
                bool exists;
                Publish(ReadFile(out exists), null, null);
            }
            catch (Exception ex)
            {
                storageError = "Could not load the rules. " + StorageFailure(ex);
            }
        }

        public string LastError
        {
            get { lock (stateGate) { return storageError.Length != 0 ? storageError : runtimeError; } }
        }

        public Dictionary<string, string> Values(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(path)) return result;
            lock (stateGate)
            {
                Dictionary<string, Desired> settings;
                if (rules.TryGetValue(path, out settings))
                    foreach (var item in settings) result.Add(item.Key, item.Value.Value);
            }
            return result;
        }

        // A full-path rule takes precedence only for the individual settings it defines.
        private static Dictionary<string, Desired> Matched(Dictionary<string, Dictionary<string, Desired>> source,
            string path, string basename)
        {
            Dictionary<string, Desired> exact = null, named = null;
            if (!string.IsNullOrEmpty(path))
            {
                source.TryGetValue(path, out exact);
                basename = Path.GetFileName(path);
            }
            if (!string.IsNullOrEmpty(basename)) source.TryGetValue(basename, out named);
            if (exact == null) return named;
            if (named == null || ReferenceEquals(named, exact)) return exact;
            var combined = new Dictionary<string, Desired>(named, StringComparer.Ordinal);
            foreach (var setting in exact) combined[setting.Key] = setting.Value;
            return combined;
        }

        public Dictionary<string, string> EffectiveValues(string path, string basename)
        {
            lock (stateGate)
            {
                var matched = Matched(rules, path, basename);
                return matched == null ? new Dictionary<string, string>(StringComparer.Ordinal) :
                    matched.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);
            }
        }

        public string TargetForSetting(string path, string basename, string key)
        {
            lock (stateGate)
            {
                Dictionary<string, Desired> settings;
                if (!string.IsNullOrEmpty(path))
                {
                    if (rules.TryGetValue(path, out settings) && settings.ContainsKey(key)) return path;
                    basename = Path.GetFileName(path);
                }
                if (!string.IsNullOrEmpty(basename) && rules.TryGetValue(basename, out settings) && settings.ContainsKey(key))
                    return basename;
                return path;
            }
        }

        public List<string> Paths()
        {
            lock (stateGate) { return rules.Keys.OrderBy(p => p, PathsComparer).ToList(); }
        }

        public void Set(string path, string key, string value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A setting name is required.");
            Merge(path, new Dictionary<string, string> { { key, value } });
        }

        public void Merge(string path, IDictionary<string, string> values)
        {
            if (values == null) throw new ArgumentNullException("values");
            var normalized = NormalizeValues(values);
            if (normalized.Count == 0) return;
            Change(path, normalized.Keys, settings => AddSettings(settings, normalized));
        }

        private static Dictionary<string, string> NormalizeValues(IDictionary<string, string> values)
        {
            return values.ToDictionary(pair => pair.Key, pair => CpuSetRule.Store(pair.Key, pair.Value), StringComparer.Ordinal);
        }

        private static bool AddSettings(Dictionary<string, string> settings, Dictionary<string, string> updates)
        {
            bool changed = false;
            foreach (var item in updates)
            {
                string old;
                if (!settings.TryGetValue(item.Key, out old) || old != item.Value) changed = true;
                settings[item.Key] = item.Value;
            }
            return changed;
        }

        public void MergeMany(IDictionary<string, Dictionary<string, string>> additions)
        {
            if (additions == null) throw new ArgumentNullException("additions");
            var updates = new Dictionary<string, Dictionary<string, string>>(PathsComparer);
            foreach (var entry in additions)
            {
                var values = NormalizeValues(entry.Value);
                if (values.Count != 0) updates.Add(CheckSelector(entry.Key), values);
            }
            if (updates.Count == 0) return;
            WriteChanges(null, null, delegate(Dictionary<string, Dictionary<string, string>> current)
            {
                bool changed = false;
                foreach (var entry in updates)
                {
                    Dictionary<string, string> settings;
                    if (!current.TryGetValue(entry.Key, out settings))
                        current.Add(entry.Key, settings = new Dictionary<string, string>(StringComparer.Ordinal));
                    changed |= AddSettings(settings, entry.Value);
                }
                return changed;
            });
        }

        public void Remove(string path, string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A setting name is required.");
            Change(path, null, settings => settings.Remove(key));
        }

        public void RemoveAll(string path)
        {
            Change(path, null, delegate(Dictionary<string, string> settings)
            {
                bool changed = settings.Count != 0;
                settings.Clear();
                return changed;
            });
        }

        public void ApplyOnce(int pid, long started, string path, string key, string value)
        {
            ApplyOnce(pid, started, path, null, key, value);
        }

        public void ApplyOnce(int pid, long started, string path, string basename, string key, string value)
        {
            lock (stateGate)
            {
                Controls.Apply(pid, started, key, Controls.Normalize(key, value));
                var settings = Matched(rules, path, basename);
                Desired saved;
                if (settings != null && settings.TryGetValue(key, out saved))
                    overrides[Tuple.Create(pid, started, key)] = saved;
            }
        }

        private static string CheckName(string name)
        {
            if (name == null || name.Length <= 4 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("A filename rule requires an exact executable name ending in .exe.");
            return name;
        }

        private static string CheckSelector(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("An executable path or name is required.");
            return Path.IsPathRooted(selector) ? CheckPath(selector) : CheckName(selector);
        }

        private static string CheckPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An executable path is required.");
            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 3)
                throw new ArgumentException("A rule requires the full path to the executable.");
            return Path.GetFullPath(path);
        }

        private void Change(string rawPath, ICollection<string> forceKeys, Func<Dictionary<string, string>, bool> edit)
        {
            string path = CheckSelector(rawPath);
            WriteChanges(path, forceKeys, delegate(Dictionary<string, Dictionary<string, string>> current)
            {
                Dictionary<string, string> settings;
                if (!current.TryGetValue(path, out settings)) settings = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!edit(settings)) return false;
                if (settings.Count == 0) current.Remove(path);
                else current[path] = settings;
                return true;
            });
        }

        private void WriteChanges(string forcePath, ICollection<string> forceKeys,
            Func<Dictionary<string, Dictionary<string, string>>, bool> edit)
        {
            lock (writeGate)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    // Serializes writers in different Windows sessions as well as separate copies of the app.
                    using (var writerLock = OpenWriteLock())
                    {
                        bool exists;
                        var current = ReadFile(out exists);
                        if (edit(current)) Save(current, exists);
                        Publish(current, forcePath, forceKeys);
                    }
                }
                catch (Exception ex)
                {
                    string message = "Could not save the rules. " + StorageFailure(ex);
                    lock (stateGate) { storageError = message; }
                    throw new InvalidOperationException(message, ex);
                }
            }
        }

        private FileStream OpenWriteLock()
        {
            string lockPath = Path.Combine(directory, "rules.lock");
            for (int retry = 0; ; retry++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex)
                {
                    int error = ex.HResult & 0xffff;
                    if (retry == 30 || (error != 32 && error != 33)) throw;
                    Thread.Sleep(20);
                }
            }
        }

        private static string StorageFailure(Exception ex)
        {
            var xml = ex as XmlException;
            if (xml != null)
                return string.Format(CultureInfo.InvariantCulture, "Invalid XML in rules.xml at line {0}, column {1}.",
                    xml.LineNumber, xml.LinePosition);
            if (ex is UnauthorizedAccessException) return "Access to the rules folder was denied.";
            if (ex is IOException && !(ex is InvalidDataException))
            {
                if (((uint)ex.HResult & 0xffff0000u) == 0x80070000u)
                    return WindowsError.FromCode(ex.HResult & 0xffff).Message;
                return "File system error (HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").";
            }
            return ex.Message;
        }

        private Dictionary<string, Dictionary<string, string>> ReadFile(out bool exists)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(PathsComparer);
            exists = false;
            FileStream stream;
            try { stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
            catch (FileNotFoundException) { return result; }
            catch (DirectoryNotFoundException) { return result; }
            exists = true;
            var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                IgnoreComments = true, IgnoreWhitespace = true, IgnoreProcessingInstructions = true };
            var doc = new XmlDocument { XmlResolver = null };
            using (stream)
            using (XmlReader reader = XmlReader.Create(stream, options))
                doc.Load(reader);
            XmlElement root = doc.DocumentElement;
            string version = root == null ? "" : root.GetAttribute("version");
            if (root == null || root.Name != "rules" || (version != "1" && version != "2") || root.Attributes.Count != 1)
                throw new InvalidDataException("The rules.xml file uses an unrecognized format.");
            foreach (XmlNode node in root.ChildNodes)
            {
                var program = node as XmlElement;
                if (program == null || program.Name != "program" || program.Attributes.Count != 1 ||
                    !(program.HasAttribute("path") || (version == "2" && program.HasAttribute("name"))))
                    throw new InvalidDataException("The rules.xml file contains an invalid program entry.");
                string path = program.HasAttribute("path") ? CheckPath(program.GetAttribute("path")) : CheckName(program.GetAttribute("name"));
                if (result.ContainsKey(path)) throw new InvalidDataException("The rules.xml file contains duplicate entries for the same program.");
                var settings = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (XmlNode child in program.ChildNodes)
                {
                    var setting = child as XmlElement;
                    if (setting == null || setting.Name != "setting" || setting.HasChildNodes ||
                        setting.Attributes.Count != 2 || !setting.HasAttribute("value") || !setting.HasAttribute("key"))
                        throw new InvalidDataException("The rules.xml file contains an invalid setting.");
                    string key = setting.GetAttribute("key");
                    if (string.IsNullOrEmpty(key) || settings.ContainsKey(key))
                        throw new InvalidDataException("The rules.xml file contains an empty or duplicate setting name.");
                    settings.Add(key, CpuSetRule.NormalizeStored(key, setting.GetAttribute("value")));
                }
                if (settings.Count == 0) throw new InvalidDataException("A program entry in rules.xml has no settings.");
                result.Add(path, settings);
            }
            return result;
        }

        private void Save(Dictionary<string, Dictionary<string, string>> state, bool exists)
        {
            string temp = Path.Combine(directory, "rules-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                    {
                        writer.WriteStartElement("rules");
                        writer.WriteAttributeString("version", state.Keys.Any(k => !Path.IsPathRooted(k)) ? "2" : "1");
                        foreach (var program in state.OrderBy(p => p.Key, PathsComparer))
                        {
                            writer.WriteStartElement("program");
                            writer.WriteAttributeString(Path.IsPathRooted(program.Key) ? "path" : "name", program.Key);
                            foreach (var setting in program.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
                            {
                                writer.WriteStartElement("setting");
                                writer.WriteAttributeString("key", setting.Key);
                                writer.WriteAttributeString("value", setting.Value);
                                writer.WriteEndElement();
                            }
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    output.Flush(true);
                }
                if (exists) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void Publish(Dictionary<string, Dictionary<string, string>> newRules, string forcePath, ICollection<string> forceKeys)
        {
            lock (stateGate)
            {
                var next = new Dictionary<string, Dictionary<string, Desired>>(PathsComparer);
                foreach (var program in newRules)
                {
                    var values = new Dictionary<string, Desired>(StringComparer.Ordinal);
                    Dictionary<string, Desired> previous;
                    rules.TryGetValue(program.Key, out previous);
                    foreach (var item in program.Value)
                    {
                        Desired old;
                        bool force = forceKeys != null && PathsComparer.Equals(program.Key, forcePath) && forceKeys.Contains(item.Key);
                        values[item.Key] = !force && previous != null && previous.TryGetValue(item.Key, out old) && old.Value == item.Value
                            ? old : new Desired(item.Value);
                    }
                    next.Add(program.Key, values);
                }
                rules = next;
                storageError = "";
                runtimeError = "";
            }
        }

        public void Reconcile(IList<ProcessRow> snapshot)
        {
            if (snapshot == null || Interlocked.CompareExchange(ref reconciling, 1, 0) != 0) return;
            try
            {
                Dictionary<string, Dictionary<string, Desired>> captured;
                Dictionary<Tuple<int, long, string>, Desired> previousOverrides;
                lock (stateGate)
                {
                    captured = rules;
                    previousOverrides = new Dictionary<Tuple<int, long, string>, Desired>(overrides);
                }
                var next = new Dictionary<Tuple<int, long, string>, Attempt>();
                string firstError = "";
                int errors = 0;
                string firstUnverified = "";
                int unverified = 0;
                if (captured.Count != 0)
                {
                    DateTime now = DateTime.UtcNow;
                    foreach (var row in snapshot)
                    {
                        if (row.Pid <= 0) continue;
                        var settings = Matched(captured, row.Path, row.Name);
                        if (settings == null) continue;
                        if (row.Started <= 0)
                        {
                            if (unverified++ == 0)
                                firstUnverified = row.Name + " (PID " + row.Pid.ToString(CultureInfo.InvariantCulture) + ")";
                            continue;
                        }
                        foreach (var setting in settings)
                        {
                            var id = Tuple.Create(row.Pid, row.Started, setting.Key);
                            Attempt attempt;
                            Desired skip;
                            if (previousOverrides.TryGetValue(id, out skip) && skip == setting.Value)
                            {
                                attempt = new Attempt { Rule = setting.Value };
                            }
                            else if (!attempts.TryGetValue(id, out attempt) || attempt.Rule != setting.Value ||
                                (attempt.Error != null && now >= attempt.NextTry))
                            {
                                attempt = new Attempt { Rule = setting.Value };
                                try
                                {
                                    string observed = Controls.Read(row.Pid, row.Started, setting.Key);
                                    string desired=CpuSetRule.Resolve(row.Pid,row.Started,setting.Key,setting.Value.Value);
                                    if (!string.Equals(observed, desired, StringComparison.OrdinalIgnoreCase))
                                    {
                                        lock (stateGate)
                                        {
                                            var live = Matched(rules, row.Path, row.Name);
                                            Desired current;
                                            if (live == null || !live.TryGetValue(setting.Key, out current) || current != setting.Value) continue;
                                            if (!overrides.TryGetValue(id, out skip) || skip != current)
                                                Controls.Apply(row.Pid, row.Started, setting.Key, desired);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    attempt.Error = (string.IsNullOrEmpty(row.Path) ? row.Name : Path.GetFileName(row.Path)) + " (PID " +
                                        row.Pid.ToString(CultureInfo.InvariantCulture) + ") — " + ex.Message;
                                    attempt.NextTry = now.AddSeconds(30);
                                }
                            }
                            next[id] = attempt;
                            if (attempt.Error != null)
                            {
                                if (errors == 0) firstError = attempt.Error;
                                errors++;
                            }
                        }
                    }
                }
                attempts = next;
                lock (stateGate)
                {
                    if (previousOverrides.Count != 0)
                    {
                        foreach (var saved in previousOverrides)
                        {
                            Desired active;
                            Attempt seen;
                            if (overrides.TryGetValue(saved.Key, out active) && active == saved.Value &&
                                (!next.TryGetValue(saved.Key, out seen) || seen.Rule != active))
                                overrides.Remove(saved.Key);
                        }
                    }
                    if (ReferenceEquals(captured, rules))
                    {
                        runtimeError = errors == 0 ? "" : "Settings awaiting retry (" + errors + ") — " + firstError;
                        if (unverified > 0)
                            runtimeError += (errors == 0 ? "" : "  |  ") + "Windows could not verify " + unverified +
                                (unverified == 1 ? " matching process" : " matching processes") + " — " + firstUnverified;
                    }
                }
            }
            finally { Interlocked.Exchange(ref reconciling, 0); }
        }
    }
}
