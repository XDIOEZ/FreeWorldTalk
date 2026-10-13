using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FreeWorldTalk
{
    public sealed class TalkSettings
    {
        public string Endpoint = "https://api.deepseek.com";
        public string Model = "deepseek-chat";
        public string Language = "中文";
        [JsonIgnore] public string ApiKey = "";
        public bool AutoEnabled = true;
        public bool UseCharacterProfile = true;
        public int AutoIntervalSeconds = 60;
        public int RequestTimeoutSeconds = 45;
        public int MaxOutputTokens = 900;
        public int RecentTurns = 12;
        public int MaxMemoriesPerActor = 32;
        public int MaxCacheEntries = 128;
        public int CacheTtlMinutes = 60;
        public string WorldDescription = "";
    }

    public sealed class ActorSnapshot
    {
        public long Uid;
        public string Name;
        public string Profile;
    }

    public sealed class TalkLine
    {
        public long SpeakerUid;
        public string Text;
    }

    public sealed class DialogueRequest
    {
        [JsonIgnore] public string SystemPrompt;
        [JsonIgnore] public string RequestInstruction;
        public string Mode;
        // One model request plays exactly this actor; shared facts can still describe other actors.
        public long SpeakerUid;
        public string Topic;
        public string MapContext;
        public string Memory;
        public string PlayerText;
        public List<ActorSnapshot> Actors = new List<ActorSnapshot>();
        public List<TalkLine> SeedLines = new List<TalkLine>();
    }

    public sealed class DialogueResponse
    {
        public List<TalkLine> Lines = new List<TalkLine>();
        public int PromptTokens;
        public int CompletionTokens;
        public bool FromCache;
    }

    public sealed class RolePromptRequest
    {
        [JsonIgnore] public string SystemPrompt;
        [JsonIgnore] public string RequestInstruction;
        public ActorSnapshot Actor;
    }

    public sealed class RolePromptResponse
    {
        public string Prompt;
        public int PromptTokens;
        public int CompletionTokens;
    }

    public sealed class HistoryTurn
    {
        // Optional in older Version 1 checkpoints; assigned when those checkpoints load.
        public string RecordId;
        public bool EditedByPlayer;
        public long SimulationMM;
        public string Speaker;
        public string Text;
    }

    public sealed class CharacterMemoryRecord
    {
        public string RecordId;
        public string Kind;
        public bool EditedByPlayer;
        public long SimulationMM;
        public string Speaker;
        public string Text;
    }
}
