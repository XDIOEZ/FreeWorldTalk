using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace FreeWorldTalk
{
    // Contains only detached text/value records. Never keeps Unity objects or game beans.
    public sealed class MemoryStore
    {
        private const int MaxActors = 256;
        public const int MaxRecordCharacters = 2000;
        private const int MaxContextCharacters = 12000;
        private const long MaxFileBytes = 32L * 1024 * 1024;
        private readonly object gate = new object();
        private readonly string root;
        private readonly TalkSettings settings;
        private Dictionary<long, ActorMemory> actors = new Dictionary<long, ActorMemory>();
        private Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>();

        public MemoryStore(string root, TalkSettings settings)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("记忆目录不能为空。", "root");
            SettingsStore.Validate(settings);
            this.root = Path.GetFullPath(root);
            this.settings = settings;
            string path = Path.Combine(this.root, "cache.json");
            if (File.Exists(path))
            {
                CacheFile file = ReadJson<CacheFile>(path);
                if (file == null || file.Version != 1 || file.Entries == null)
                    throw new InvalidDataException("FreeWorldTalk 缓存文件结构无效。");
                foreach (var entry in file.Entries)
                {
                    if (entry == null || !IsHash(entry.Key) || entry.Response == null || entry.Response.Lines == null
                        || entry.CreatedUtcTicks <= 0 || entry.CreatedUtcTicks > DateTime.MaxValue.Ticks
                        || cache.ContainsKey(entry.Key))
                        throw new InvalidDataException("FreeWorldTalk 缓存记录无效。");
                    ValidateStoredLines(entry.Response.Lines);
                    cache.Add(entry.Key, entry);
                }
                TrimCache(settings);
            }
        }

        public void LoadCheckpoint(string key)
        {
            string path = CheckpointPath(key);
            lock (gate)
            {
                // A missing sidecar means an older/unmodded save or a fresh campaign.
                // It must not inherit another save's memories.
                if (!File.Exists(path))
                {
                    actors = new Dictionary<long, ActorMemory>();
                    return;
                }
                Checkpoint checkpoint = ReadJson<Checkpoint>(path);
                actors = ValidateCheckpoint(checkpoint);
            }
        }

        public void Clear()
        {
            lock (gate) actors.Clear();
        }

        public string ExportCheckpoint(long simulationMM)
        {
            lock (gate)
            {
                var checkpoint = new Checkpoint { Version = 1, SimulationMM = simulationMM };
                foreach (ActorMemory actor in actors.Values.OrderBy(x => x.Uid))
                {
                    var copy = new ActorMemory { Uid = actor.Uid, Name = actor.Name, LastSeenMM = Math.Min(actor.LastSeenMM, simulationMM) };
                    copy.History = actor.History.Where(x => x.SimulationMM <= simulationMM)
                        .Select(CloneTurn).ToList();
                    copy.Events = actor.Events.Where(x => x.SimulationMM <= simulationMM)
                        .Select(CloneTurn).ToList();
                    if (copy.History.Count != 0 || copy.Events.Count != 0) checkpoint.Actors.Add(copy);
                }
                return JsonConvert.SerializeObject(checkpoint, Formatting.None);
            }
        }

        public string FinalizeCheckpoint(string json, long simulationMM)
        {
            if (simulationMM < 0 || simulationMM == Int64.MaxValue)
                throw new ArgumentOutOfRangeException("simulationMM", "必须使用游戏存档快照中的真实模拟时间。");
            if (String.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                throw new InvalidDataException("FreeWorldTalk 存档记忆快照为空或过大。");
            Checkpoint checkpoint;
            try { checkpoint = JsonConvert.DeserializeObject<Checkpoint>(json); }
            catch (JsonException) { throw new InvalidDataException("FreeWorldTalk 存档记忆快照 JSON 无效。"); }
            // Validate the frozen payload without consulting/merging any live actors.
            // Do not trim before the time filter, which could discard valid older turns.
            ValidateCheckpoint(checkpoint, false);
            checkpoint.SimulationMM = simulationMM;
            foreach (ActorMemory actor in checkpoint.Actors)
            {
                actor.History = actor.History.Where(x => x.SimulationMM <= simulationMM).ToList();
                actor.Events = actor.Events.Where(x => x.SimulationMM <= simulationMM).ToList();
                actor.LastSeenMM = Math.Min(actor.LastSeenMM, simulationMM);
            }
            checkpoint.Actors.RemoveAll(x => x.History.Count == 0 && x.Events.Count == 0);
            return JsonConvert.SerializeObject(checkpoint, Formatting.None);
        }

        public void WriteCheckpoint(string key, string json)
        {
            if (String.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                throw new InvalidDataException("FreeWorldTalk 存档记忆快照为空或过大。");
            Checkpoint checkpoint;
            try { checkpoint = JsonConvert.DeserializeObject<Checkpoint>(json); }
            catch (JsonException) { throw new InvalidDataException("FreeWorldTalk 存档记忆快照 JSON 无效。"); }
            ValidateCheckpoint(checkpoint, false);
            string path = CheckpointPath(key);
            // Validation upgrades missing record IDs from old Version 1 payloads.
            lock (gate) SettingsStore.AtomicWrite(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(checkpoint, Formatting.None)));
        }

        public string ContextFor(long[] uids, long simTime)
        {
            if (uids == null) throw new ArgumentNullException("uids");
            lock (gate)
            {
                var records = new List<ContextRecord>();
                foreach (long uid in uids.Distinct())
                {
                    ActorMemory actor;
                    if (!actors.TryGetValue(uid, out actor)) continue;
                    foreach (HistoryTurn turn in actor.History.Where(x => x.SimulationMM <= simTime).TakeLastCompatible(settings.RecentTurns * 2))
                        records.Add(new ContextRecord { Uid = uid, Name = actor.Name, Kind = "对话记录", Turn = turn });
                    foreach (HistoryTurn turn in actor.Events.Where(x => x.SimulationMM <= simTime))
                        records.Add(new ContextRecord { Uid = uid, Name = actor.Name, Kind = "观察记忆", Turn = turn });
                }
                var picked = new List<string>();
                int length = 0;
                foreach (ContextRecord item in records.OrderByDescending(x => x.Turn.SimulationMM))
                {
                    string line = "角色 " + item.Uid + "（" + item.Name + "），模拟时间 " + item.Turn.SimulationMM
                        + "，" + item.Kind + (item.Turn.EditedByPlayer ? "（玩家编辑的记忆）" : "")
                        + "，" + item.Turn.Speaker + "：" + item.Turn.Text;
                    if (length + line.Length + 1 > MaxContextCharacters) continue;
                    picked.Add(line);
                    length += line.Length + 1;
                }
                picked.Reverse();
                return String.Join("\n", picked.ToArray());
            }
        }

        public List<HistoryTurn> HistoryFor(long uid)
        {
            lock (gate)
            {
                ActorMemory actor;
                return actors.TryGetValue(uid, out actor) ? actor.History.Select(CloneTurn).ToList() : new List<HistoryTurn>();
            }
        }

        public List<CharacterMemoryRecord> RecordsFor(long uid, long simulationMM)
        {
            ValidateIdentity(uid, simulationMM);
            lock (gate)
            {
                ActorMemory actor;
                if (!actors.TryGetValue(uid, out actor)) return new List<CharacterMemoryRecord>();
                return actor.History.Select(x => SnapshotRecord(x, "history"))
                    .Concat(actor.Events.Select(x => SnapshotRecord(x, "event")))
                    .Where(x => x.SimulationMM <= simulationMM).OrderBy(x => x.SimulationMM).ToList();
            }
        }

        public bool TryEditRecord(long uid, CharacterMemoryRecord expected, string text, long simulationMM, out string reason)
        {
            ValidateIdentity(uid, simulationMM);
            ValidateExpected(expected);
            RequiredPlainText(text, MaxRecordCharacters, "记忆内容");
            lock (gate)
            {
                ActorMemory actor;
                HistoryTurn turn;
                if (!FindExpectedRecord(uid, expected, simulationMM, out actor, out turn, out reason)) return false;
                turn.Text = text;
                turn.EditedByPlayer = true;
                actor.LastSeenMM = simulationMM;
                return true;
            }
        }

        public bool TryDeleteRecord(long uid, CharacterMemoryRecord expected, long simulationMM, out string reason)
        {
            ValidateIdentity(uid, simulationMM);
            ValidateExpected(expected);
            lock (gate)
            {
                ActorMemory actor;
                HistoryTurn turn;
                if (!FindExpectedRecord(uid, expected, simulationMM, out actor, out turn, out reason)) return false;
                (expected.Kind == "history" ? actor.History : actor.Events).Remove(turn);
                actor.LastSeenMM = simulationMM;
                if (actor.History.Count == 0 && actor.Events.Count == 0) actors.Remove(uid);
                return true;
            }
        }

        public void AddManualMemory(long uid, string name, string text, long simulationMM)
        {
            ValidateIdentity(uid, simulationMM);
            RequiredPlainText(name, 128, "角色名称");
            RequiredPlainText(text, MaxRecordCharacters, "记忆内容");
            lock (gate)
            {
                ActorMemory actor = GetActor(uid, simulationMM);
                actor.Name = name;
                HistoryTurn turn = NewTurn(simulationMM, "玩家编辑", text);
                turn.EditedByPlayer = true;
                actor.Events.Add(turn);
                TrimList(actor.Events, settings.MaxMemoriesPerActor);
            }
        }

        // Called under gate. Match the editor's original version of just this record,
        // allowing unrelated new observations to arrive while the player is editing.
        private bool FindExpectedRecord(long uid, CharacterMemoryRecord expected, long simulationMM,
            out ActorMemory actor, out HistoryTurn turn, out string reason)
        {
            turn = null;
            reason = null;
            if (!actors.TryGetValue(uid, out actor))
            {
                reason = "该角色的记忆已清除，请刷新列表。";
                return false;
            }
            turn = (expected.Kind == "history" ? actor.History : actor.Events).FirstOrDefault(x => x.RecordId == expected.RecordId);
            if (turn == null)
            {
                reason = "这条记忆已删除或超过保留数量，请刷新列表。";
                return false;
            }
            if (turn.SimulationMM > simulationMM || turn.SimulationMM != expected.SimulationMM
                || turn.Speaker != expected.Speaker || turn.Text != expected.Text || turn.EditedByPlayer != expected.EditedByPlayer)
            {
                reason = "这条记忆已变化，请刷新后再编辑。";
                return false;
            }
            return true;
        }

        public void RememberPlayer(long uid, string name, string playerText, string reply, long simTime)
        {
            RequiredText(name, 128, "角色名称");
            RequiredText(playerText, DeepSeekClient.MaxPlayerCharacters, "玩家台词");
            RequiredText(reply, DeepSeekClient.MaxLineCharacters, "角色回复");
            lock (gate)
            {
                ActorMemory actor = GetActor(uid, simTime);
                actor.Name = name;
                actor.History.Add(NewTurn(simTime, "玩家", playerText));
                actor.History.Add(NewTurn(simTime, name, reply));
                TrimList(actor.History, settings.RecentTurns * 2);
            }
        }

        public void RememberSocial(List<TalkLine> lines, string topic, long simTime, long[] participantUids = null)
        {
            ValidateStoredLines(lines);
            if ((topic ?? "").Length > 500) throw new ArgumentException("闲聊主题过长。");
            long[] listeners = (participantUids ?? lines.Select(x => x.SpeakerUid).ToArray()).Distinct().ToArray();
            if (listeners.Length == 0 || listeners.Length > 32) throw new ArgumentException("闲聊参与者数量无效。");
            if (lines.Any(x => !listeners.Contains(x.SpeakerUid))) throw new ArgumentException("闲聊发言者不属于参与者。");
            lock (gate)
            {
                foreach (long uid in listeners)
                {
                    ActorMemory actor = GetActor(uid, simTime);
                    foreach (TalkLine line in lines)
                        actor.Events.Add(NewTurn(simTime, "角色 " + line.SpeakerUid,
                            "闲聊主题：" + (topic ?? "") + "；台词：" + line.Text));
                    TrimList(actor.Events, settings.MaxMemoriesPerActor);
                }
            }
        }

        public void RememberEvent(long uid, string text, long simTime)
        {
            RequiredText(text, MaxRecordCharacters, "观察事件");
            lock (gate)
            {
                ActorMemory actor = GetActor(uid, simTime);
                HistoryTurn last = actor.Events.LastOrDefault();
                if (last != null && last.Text == text && last.SimulationMM == simTime) return;
                actor.Events.Add(NewTurn(simTime, "游戏事实", text));
                TrimList(actor.Events, settings.MaxMemoriesPerActor);
            }
        }

        public bool TryGetCache(DialogueRequest req, TalkSettings settings, out DialogueResponse response)
        {
            SettingsStore.Validate(settings);
            DeepSeekClient.ValidateRequest(req);
            string key = CacheKey(req, settings);
            lock (gate)
            {
                TrimCache(settings);
                CacheEntry entry;
                if (!cache.TryGetValue(key, out entry)) { response = null; return false; }
                response = CloneResponse(entry.Response);
                DeepSeekClient.ValidateResponse(req, response);
                response.FromCache = true;
                return true;
            }
        }

        public void PutCache(DialogueRequest req, TalkSettings settings, DialogueResponse response)
        {
            SettingsStore.Validate(settings);
            DeepSeekClient.ValidateRequest(req);
            DeepSeekClient.ValidateResponse(req, response);
            string key = CacheKey(req, settings);
            lock (gate)
            {
                cache[key] = new CacheEntry { Key = key, CreatedUtcTicks = DateTime.UtcNow.Ticks, Response = CloneResponse(response) };
                TrimCache(settings);
                var file = new CacheFile { Version = 1, Entries = cache.Values.OrderBy(x => x.CreatedUtcTicks).ToList() };
                SettingsStore.AtomicWrite(Path.Combine(root, "cache.json"), Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(file)));
            }
        }

        private void TrimCache(TalkSettings current)
        {
            long now = DateTime.UtcNow.Ticks;
            long oldest = now - TimeSpan.FromMinutes(current.CacheTtlMinutes).Ticks;
            foreach (string key in cache.Where(x => x.Value.CreatedUtcTicks < oldest || x.Value.CreatedUtcTicks > now).Select(x => x.Key).ToList())
                cache.Remove(key);
            foreach (string key in cache.OrderByDescending(x => x.Value.CreatedUtcTicks).Skip(current.MaxCacheEntries).Select(x => x.Key).ToList())
                cache.Remove(key);
        }

        private ActorMemory GetActor(long uid, long simTime)
        {
            ValidateIdentity(uid, simTime);
            ActorMemory actor;
            if (!actors.TryGetValue(uid, out actor))
            {
                if (actors.Count >= MaxActors)
                    actors.Remove(actors.Values.OrderBy(x => x.LastSeenMM).ThenBy(x => x.Uid).First().Uid);
                actor = new ActorMemory { Uid = uid, Name = "角色 " + uid };
                actors.Add(uid, actor);
            }
            actor.LastSeenMM = simTime;
            return actor;
        }

        private Dictionary<long, ActorMemory> ValidateCheckpoint(Checkpoint checkpoint, bool trim = true)
        {
            if (checkpoint == null || checkpoint.Version != 1 || checkpoint.SimulationMM < 0
                || checkpoint.Actors == null || checkpoint.Actors.Count > MaxActors)
                throw new InvalidDataException("FreeWorldTalk 存档记忆结构或版本无效。");
            var result = new Dictionary<long, ActorMemory>();
            foreach (ActorMemory actor in checkpoint.Actors)
            {
                if (actor == null || actor.Uid <= 0 || result.ContainsKey(actor.Uid) || actor.History == null || actor.Events == null
                    || actor.History.Count > 100 || actor.Events.Count > 100 || String.IsNullOrWhiteSpace(actor.Name) || actor.Name.Length > 128
                    || actor.LastSeenMM < 0 || actor.LastSeenMM > checkpoint.SimulationMM)
                    throw new InvalidDataException("FreeWorldTalk 存档角色记忆字段无效。");
                var recordIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (HistoryTurn turn in actor.History.Concat(actor.Events))
                {
                    if (turn == null || turn.SimulationMM < 0 || turn.SimulationMM > checkpoint.SimulationMM || String.IsNullOrWhiteSpace(turn.Speaker)
                        || turn.Speaker.Length > 128 || String.IsNullOrWhiteSpace(turn.Text) || turn.Text.Length > MaxRecordCharacters)
                        throw new InvalidDataException("FreeWorldTalk 存档对话或事件记忆无效。");
                    if (String.IsNullOrEmpty(turn.RecordId)) turn.RecordId = NewRecordId();
                    if (!IsRecordId(turn.RecordId) || !recordIds.Add(turn.RecordId))
                        throw new InvalidDataException("FreeWorldTalk 存档记忆 RecordId 无效或在同一角色下重复。");
                }
                if (trim)
                {
                    TrimList(actor.History, settings.RecentTurns * 2);
                    TrimList(actor.Events, settings.MaxMemoriesPerActor);
                }
                result.Add(actor.Uid, actor);
            }
            return result;
        }

        private string CheckpointPath(string key)
        {
            if (String.IsNullOrWhiteSpace(key) || key.Length > 4096) throw new ArgumentException("存档记忆标识为空或过长。");
            return Path.Combine(root, "checkpoints", Hash(key) + ".json");
        }

        private static string CacheKey(DialogueRequest req, TalkSettings settings)
        {
            // Hash the entire immutable request context and all generation parameters;
            // key excludes ApiKey and never stores raw prompts in the cache index.
            return Hash(JsonConvert.SerializeObject(new { Version = 3, Provider = settings.Endpoint.TrimEnd('/'),
                settings.Model, settings.Language, settings.WorldDescription, settings.UseCharacterProfile, settings.MaxOutputTokens, Temperature = 0.8,
                req.SystemPrompt, req.RequestInstruction, Request = req }));
        }

        private static string Hash(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsHash(string value)
        {
            return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        private static T ReadJson<T>(string path)
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("FreeWorldTalk 数据文件超过大小限制。");
            try { return JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (JsonException) { throw new InvalidDataException("FreeWorldTalk 数据文件 JSON 损坏：" + Path.GetFileName(path)); }
        }

        private static void RequiredText(string value, int limit, string label)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > limit) throw new ArgumentException(label + "为空或超过长度限制。");
        }

        private static void RequiredPlainText(string value, int limit, string label)
        {
            RequiredText(value, limit, label);
            if (value.Any(c => Char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                throw new ArgumentException(label + "含不支持的控制字符。");
        }

        private static void ValidateIdentity(long uid, long simulationMM)
        {
            if (uid <= 0) throw new ArgumentOutOfRangeException("uid", "角色 UID 必须大于零。");
            if (simulationMM < 0 || simulationMM == Int64.MaxValue)
                throw new ArgumentOutOfRangeException("simulationMM", "必须使用当前游戏的真实模拟时间。");
        }

        private static void ValidateExpected(CharacterMemoryRecord expected)
        {
            if (expected == null) throw new ArgumentNullException("expected");
            if (!IsRecordId(expected.RecordId) || (expected.Kind != "history" && expected.Kind != "event")
                || expected.SimulationMM < 0 || expected.SimulationMM == Int64.MaxValue)
                throw new ArgumentException("记忆记录的标识、类型或时间无效。", "expected");
            RequiredText(expected.Speaker, 128, "记忆来源");
            RequiredText(expected.Text, MaxRecordCharacters, "原记忆内容");
        }

        private static bool IsRecordId(string value)
        {
            return value != null && value.Length == 32
                && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        private static string NewRecordId() { return Guid.NewGuid().ToString("N"); }

        private static HistoryTurn NewTurn(long simulationMM, string speaker, string text)
        {
            return new HistoryTurn { RecordId = NewRecordId(), SimulationMM = simulationMM, Speaker = speaker, Text = text };
        }

        private static CharacterMemoryRecord SnapshotRecord(HistoryTurn turn, string kind)
        {
            return new CharacterMemoryRecord { RecordId = turn.RecordId, Kind = kind, SimulationMM = turn.SimulationMM,
                Speaker = turn.Speaker, Text = turn.Text, EditedByPlayer = turn.EditedByPlayer };
        }

        private static void ValidateStoredLines(List<TalkLine> lines)
        {
            if (lines == null || lines.Count < 1 || lines.Count > 32) throw new InvalidDataException("缓存或闲聊台词数量无效。");
            foreach (TalkLine line in lines)
                if (line == null || String.IsNullOrWhiteSpace(line.Text) || line.Text.Length > DeepSeekClient.MaxLineCharacters)
                    throw new InvalidDataException("缓存或闲聊台词字段无效。");
        }

        private static HistoryTurn CloneTurn(HistoryTurn turn)
        {
            return new HistoryTurn { RecordId = turn.RecordId, SimulationMM = turn.SimulationMM, Speaker = turn.Speaker,
                Text = turn.Text, EditedByPlayer = turn.EditedByPlayer };
        }

        private static DialogueResponse CloneResponse(DialogueResponse response)
        {
            return new DialogueResponse { Lines = response.Lines.Select(x => new TalkLine { SpeakerUid = x.SpeakerUid, Text = x.Text }).ToList(),
                PromptTokens = response.PromptTokens, CompletionTokens = response.CompletionTokens, FromCache = false };
        }

        private static void TrimList<T>(List<T> list, int limit)
        {
            if (list.Count > limit) list.RemoveRange(0, list.Count - limit);
        }

        private sealed class Checkpoint
        {
            [JsonProperty(Required = Required.Always)]
            public int Version;
            [JsonProperty(Required = Required.Always)]
            public long SimulationMM;
            [JsonProperty(Required = Required.Always)]
            public List<ActorMemory> Actors = new List<ActorMemory>();
        }

        private sealed class ActorMemory
        {
            [JsonProperty(Required = Required.Always)]
            public long Uid;
            [JsonProperty(Required = Required.Always)]
            public string Name;
            [JsonProperty(Required = Required.Always)]
            public long LastSeenMM;
            [JsonProperty(Required = Required.Always)]
            public List<HistoryTurn> History = new List<HistoryTurn>();
            [JsonProperty(Required = Required.Always)]
            public List<HistoryTurn> Events = new List<HistoryTurn>();
        }

        private sealed class ContextRecord
        {
            public long Uid;
            public string Name;
            public string Kind;
            public HistoryTurn Turn;
        }

        private sealed class CacheFile
        {
            [JsonProperty(Required = Required.Always)]
            public int Version;
            [JsonProperty(Required = Required.Always)]
            public List<CacheEntry> Entries = new List<CacheEntry>();
        }

        private sealed class CacheEntry
        {
            [JsonProperty(Required = Required.Always)]
            public string Key;
            [JsonProperty(Required = Required.Always)]
            public long CreatedUtcTicks;
            [JsonProperty(Required = Required.Always)]
            public DialogueResponse Response;
        }
    }

    internal static class MemoryEnumerable
    {
        // Unity's .NET profile does not guarantee Enumerable.TakeLast.
        internal static IEnumerable<T> TakeLastCompatible<T>(this IEnumerable<T> source, int count)
        {
            var queue = new Queue<T>();
            foreach (T value in source)
            {
                queue.Enqueue(value);
                if (queue.Count > count) queue.Dequeue();
            }
            return queue;
        }
    }
}
