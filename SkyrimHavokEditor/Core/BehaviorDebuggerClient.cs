using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SkyrimHavokEditor.Core
{
    public class VariableValue
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("value")] public float Value { get; set; }
    }

    public class ActiveStateInfo
    {
        [JsonPropertyName("smName")] public string SMName { get; set; }
        [JsonPropertyName("stateId")] public int StateId { get; set; }
        [JsonPropertyName("stateName")] public string StateName { get; set; } = "";
    }

    public class BehaviorSnapshot
    {
        [JsonPropertyName("formId")] public string FormId { get; set; }
        [JsonPropertyName("actorName")] public string ActorName { get; set; }
        [JsonPropertyName("behaviorFile")] public string BehaviorFile { get; set; }
        [JsonPropertyName("activeStates")] public List<ActiveStateInfo> ActiveStates { get; set; } = new();
        [JsonPropertyName("variables")] public List<VariableValue> Variables { get; set; } = new();
        [JsonPropertyName("dragon")] public DragonSnapshot Dragon { get; set; }

        // Set by editor — not from plugin
        [JsonIgnore] public DateTime Timestamp { get; set; }
    }

    public class DragonSnapshot
    {
        [JsonPropertyName("formId")] public string FormId { get; set; }
        [JsonPropertyName("behaviorFile")] public string BehaviorFile { get; set; }
        [JsonPropertyName("activeStates")] public List<ActiveStateInfo> ActiveStates { get; set; } = new();
        [JsonPropertyName("variables")] public List<VariableValue> Variables { get; set; } = new();
    }

    public class DebugVarEntry
    {
        public string Name { get; set; }
        public string Type { get; set; }  // "float" or "int"
    }

    public class DebugSMEntry
    {
        public string VariableName { get; set; }
        public string SmName { get; set; }
    }

    public class DebugConfig
    {
        public List<DebugVarEntry> Variables { get; set; } = new();
        public List<DebugSMEntry> StateMachines { get; set; } = new();
    }

    public class BehaviorDebuggerClient
    {
        public event Action<BehaviorSnapshot> SnapshotReceived;
        public event Action<bool> ConnectionChanged;

        private Thread _thread;
        private volatile bool _running;
        private volatile bool _paused;
        private NamedPipeClientStream _activePipe;

        // Session recording
        private readonly List<BehaviorSnapshot> _recording = new();
        private bool _isRecording;

        public bool IsConnected { get; private set; }
        public bool IsPaused => _paused;
        public bool IsRecording => _isRecording;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "BehaviorDebugger" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _activePipe?.Close(); } catch { }
        }

        public void Pause() => _paused = true;
        public void Resume() => _paused = false;

        public void StartRecording()
        {
            _recording.Clear();
            _isRecording = true;
        }

        public List<BehaviorSnapshot> StopRecording()
        {
            _isRecording = false;
            return new List<BehaviorSnapshot>(_recording);
        }

        public void ExportRecording(string filePath)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var records = _recording.Select(s => new
            {
                timestamp = s.Timestamp.ToString("HH:mm:ss.fff"),
                actorName = s.ActorName,
                behaviorFile = s.BehaviorFile,
                activeStates = s.ActiveStates,
                variables = s.Variables
            }).ToList();
            File.WriteAllText(filePath, JsonSerializer.Serialize(records, options));
        }
        public void SendConfig(DebugConfig config)
        {
            Task.Run(() =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".", "SkyrimBehaviorDebugger_Config", PipeDirection.Out);
                    pipe.Connect(3000);

                    var json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        variables = config.Variables.Select(v => new
                        { name = v.Name, type = v.Type }),
                        stateMachines = config.StateMachines.Select(s => new
                        { variableName = s.VariableName, smName = s.SmName })
                    });

                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Config send failed: " + ex.Message);
                }
            });
        }
        private void WorkerLoop()
        {
            while (_running)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".", "SkyrimBehaviorDebugger", PipeDirection.In);
                    _activePipe = pipe;

                    try { pipe.Connect(2000); }
                    catch (TimeoutException) { _activePipe = null; continue; }

                    if (!_running) break;

                    IsConnected = true;
                    ConnectionChanged?.Invoke(true);

                    using var reader = new StreamReader(pipe);
                    while (_running && pipe.IsConnected)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (_paused) continue;

                        try
                        {
                            var snap = JsonSerializer.Deserialize<BehaviorSnapshot>(line);
                            if (snap != null)
                            {
                                snap.Timestamp = DateTime.Now;
                                if (_isRecording) _recording.Add(snap);
                                SnapshotReceived?.Invoke(snap);
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Pipe: " + ex.Message);
                }

                _activePipe = null;
                IsConnected = false;
                ConnectionChanged?.Invoke(false);
                if (_running) Thread.Sleep(1000);
            }
        }
    }
}
