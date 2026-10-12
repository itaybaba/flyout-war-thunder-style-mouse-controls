// Request handlers for the MouseAim file interface (McpBridge/MouseAim/requests/<guid>.json).
// Ops: status, enable, release, config, aim, record, stop_record, reset_model.

using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace FlyoutMouseAim
{
    static class Commands
    {
        public static object Execute(MouseAimMod mod, JsonElement req)
        {
            string op = req.GetProperty("op").GetString();
            switch (op)
            {
                case "test_launch_setup":
                    if (Il2Cpp.Craft.active != null && Il2Cpp.Craft.active.initialized) throw new Exception("Return to the editor first.");
                    float altitude = req.GetProperty("altitude").GetSingle(), speed = req.GetProperty("speed").GetSingle();
                    if (!float.IsFinite(altitude) || !float.IsFinite(speed) || altitude < 0 || altitude > 10000 || speed < 0 || speed > 400)
                        throw new Exception("Use altitude 0..10000 m and speed 0..400 m/s.");
                    Il2Cpp.Launcher.Altitude = altitude; Il2Cpp.Launcher.Velocity = speed;
                    return new { altitude, speed };
                case "test_session":
                    return mod.TestSession(req.GetProperty("seconds").GetSingle());
                case "status":
                    return mod.Status();
                case "enable":
                    mod.SetEnabled(req.GetProperty("on").GetBoolean());
                    return mod.Status();
                case "release":
                    mod.EmergencyRelease("request");
                    return mod.Status();
                case "reset_model":
                    mod.Ins.Reset();
                    mod.Envelope.Reset();
                    return mod.Status();
                case "aim":
                    return mod.Aim(
                        req.TryGetProperty("yaw", out var y) ? y.GetSingle() : 0f,
                        req.TryGetProperty("pitch", out var p) ? p.GetSingle() : 0f,
                        req.TryGetProperty("from", out var f) ? f.GetString() : "nose");
                case "record":
                    return mod.StartRecording(req.GetProperty("seconds").GetSingle(),
                        req.TryGetProperty("name", out var n) ? n.GetString() : null);
                case "stop_record":
                    mod.StopRecording();
                    return new { stopped = true };
                case "config":
                    return Config(mod, req);
                default:
                    throw new Exception("Unknown op. Use status, enable, release, reset_model, aim, record, stop_record, config.");
            }
        }

        static object Config(MouseAimMod mod, JsonElement req)
        {
            if (req.TryGetProperty("key", out var keyEl))
            {
                string key = keyEl.GetString();
                object target = mod.S;
                FieldInfo fi = typeof(ModSettings).GetField(key, BindingFlags.Public | BindingFlags.Instance);
                if (fi == null || fi.FieldType == typeof(InstructorConfig))
                {
                    target = mod.S.Instructor;
                    fi = typeof(InstructorConfig).GetField(key, BindingFlags.Public | BindingFlags.Instance)
                         ?? throw new Exception("Unknown config key: " + key);
                }
                var val = req.GetProperty("value");
                object v = fi.FieldType == typeof(bool) ? val.GetBoolean()
                         : fi.FieldType == typeof(int) ? val.GetInt32()
                         : fi.FieldType == typeof(float) ? val.GetSingle()
                         : fi.FieldType == typeof(string) ? (object)val.GetString()
                         : throw new Exception("Unsupported type");
                if (v is float fv && !float.IsFinite(fv)) throw new Exception("Value must be finite");
                fi.SetValue(target, v);
                mod.ValidateSettings();
                mod.SaveSettings();
            }
            var all = typeof(ModSettings).GetFields().Where(f => f.FieldType != typeof(InstructorConfig))
                .ToDictionary(f => f.Name, f => f.GetValue(mod.S));
            foreach (var f in typeof(InstructorConfig).GetFields()) all[f.Name] = f.GetValue(mod.S.Instructor);
            return all;
        }
    }
}
