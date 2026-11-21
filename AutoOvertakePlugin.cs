// ETS2LA Auto Overtake — automatic overtaking for ETS2 / ATS.
// Copyright (C) 2026 baobao-7799
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

using ETS2LA.Backend.Events;
using ETS2LA.Game.Output;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using ETS2LA.Overlay;
using ETS2LA.Settings;
using ETS2LA.Shared;

using Hexa.NET.ImGui;

namespace FishLeia.AutoOvertake;

public class AutoOvertakePlugin : Plugin
{
    private const string ChannelId = "fishleia.autoovertake";
    private const string OutputTopic = "ETS2LA.Game.Output.ControlEvent";
    private const string SettingsFileName = "fishleia.autoovertake.json";
    private const string WindowTitle = "baobao-7799 自动超车";

    private enum OState { Idle, Signaling, MovingOut, Passing, Returning }

    private readonly SettingsHandler _settingsHandler = new();
    private OvertakeSettings _cfg = new();
    private volatile bool _settingsDirty;
    private float _saveTimer;

    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private float _now;

    // 本车坐标系
    private Vector3 _prevPos;
    private Vector3 _f = Vector3.UnitZ;
    private Vector3 _startPos;
    private float _lateral;
    private float _prevLat;
    private float _speed;

    // 交通车速度方向（位置差分）
    private readonly Dictionary<short, Vector3> _prevVehPos = new();
    private readonly Dictionary<short, Vector3> _vehDir = new();

    // 通行侧标定
    private int _passingSign = 1;
    private bool _sideLocked;
    private int _oncomingCount;
    private int _oncomingSignSum;

    // 转向方向运行时校准
    private int _steerSignRight = 1;
    private bool _steerSignLocked;
    private int _steerCorrCount;
    private int _steerCorrBad;

    // 状态机
    private OState _state = OState.Idle;
    private string _status = "待机";
    private float _stateTime;
    private float _cooldownUntil;

    // 目标车辆
    private short _candidateId = -1;
    private int _candidateSeen;
    private short _targetId = -1;
    private float _targetMissing;
    private float _targetFaster;
    private float _targetDist;
    private float _targetSpeed;

    // 输出
    private bool _active;
    private float _steer, _throttle, _brake;
    private bool _blinkL, _blinkR;
    private bool _danger;
    private string _dangerReason = "";
    private float _dangerVehSpeed;

    public override PluginInformation Info => new()
    {
        Id = "fishleia.autoovertake",
        Name = "baobao-7799 自动超车",
        Description = "检测前方慢车，确认邻道安全后自动打灯变道、超车并返回原车道。通行方向自动标定，支持靠左/靠右行驶。",
        Version = "1.0.0",
        AuthorName = "baobao-7799",
        AppVersionSupport = new PluginVersionSupport { LowerBound = "2026.11.0" },
        Tags = new[] { "Driving", "AI", "Gameplay" }
    };

    public override float TickRate => 30.0f;

    public override void Init()
    {
        _cfg = _settingsHandler.Load<OvertakeSettings>(SettingsFileName) ?? new OvertakeSettings();
    }

    public override void OnEnable()
    {
        base.OnEnable();
        OverlayHandler.Current.RegisterWindow(new WindowDefinition
        {
            Title = WindowTitle,
            Width = 430,
            Height = 420,
            Alpha = 0.92f,
            Open = _cfg.ShowWindow
        }, RenderUI);
        Logger.Info("AutoOvertake: enabled.");
    }

    public override void OnDisable()
    {
        ReleaseOutput();
        OverlayHandler.Current.UnregisterWindow(new WindowDefinition { Title = WindowTitle });
        base.OnDisable();
    }

    public override void Shutdown()
    {
        ReleaseOutput();
        if (_settingsDirty)
        {
            _settingsDirty = false;
            _settingsHandler.Save(SettingsFileName, _cfg);
        }
        base.Shutdown();
    }

    public override void Tick()
    {
        _now = (float)_sw.Elapsed.TotalSeconds;

        // 设置热存（1Hz 节流）
        if (_settingsDirty && (_saveTimer -= 1f / TickRate) <= 0f)
        {
            _saveTimer = 1f;
            _settingsDirty = false;
            _settingsHandler.Save(SettingsFileName, _cfg);
        }

        var cfg = _cfg;
        if (!cfg.Enabled)
        {
            ReleaseOutput();
            SetIdle("已禁用");
            return;
        }

        var td = GameTelemetry.Current.GetCurrentData();
        var traffic = TrafficProvider.Current.GetCurrentTrafficData();
        if (!td.sdkActive || td.paused || !td.truckBool.engineEnabled || td.truckBool.parkingBrake || traffic == null)
        {
            ReleaseOutput();
            SetIdle("未就绪（SDK/交通数据不可用）");
            return;
        }

        // 前向与侧向
        Vector3 pos = td.truckPlacement.coordinate.ToVector3();
        var delta = pos - _prevPos;
        if (delta.LengthSquared() > 0.01f)
            _f = Vector3.Normalize(delta);
        _prevPos = pos;
        Vector3 leftRaw = Vector3.Cross(Vector3.UnitY, _f);
        Vector3 left = leftRaw.LengthSquared() > 1e-6f ? Vector3.Normalize(leftRaw) : Vector3.UnitX;

        if (_state == OState.Idle)
        {
            _startPos = pos;
            _lateral = 0f;
            _prevLat = 0f;
        }
        else
        {
            _lateral = Vector3.Dot(pos - _startPos, left);
        }

        _speed = td.truckFloat.speed;
        float dt = 1f / TickRate;

        UpdateVehicleDirections(traffic);
        CalibratePassingSide(cfg, traffic, pos, left);

        // 用户接管：立即让出控制
        if (_state != OState.Idle &&
            (Math.Abs(td.truckFloat.userSteer) > 0.18f ||
             td.truckFloat.userThrottle > 0.25f ||
             td.truckFloat.userBrake > 0.08f))
        {
            ReleaseOutput();
            SetIdle("用户接管，已退出");
            _cooldownUntil = _now + cfg.UserCooldown;
            return;
        }

        switch (_state)
        {
            case OState.Idle: TickIdle(cfg, td, traffic, pos, left, dt); break;
            case OState.Signaling: TickSignaling(cfg, td, traffic, pos, left, dt); break;
            case OState.MovingOut: TickMovingOut(cfg, td, traffic, pos, left, dt); break;
            case OState.Passing: TickPassing(cfg, td, traffic, pos, left, dt); break;
            case OState.Returning: TickReturning(cfg, td, traffic, pos, left, dt); break;
        }

        if (_active)
            PublishOutput();

        _prevLat = _lateral;
    }

    // ---------- 各状态 ----------

    private void TickIdle(OvertakeSettings cfg, GameTelemetryData td, TrafficData traffic, Vector3 pos, Vector3 left, float dt)
    {
        _blinkL = _blinkR = false;

        if (_now < _cooldownUntil) { _status = "冷却中…"; return; }
        if (_speed < cfg.MinSpeed) { _status = $"待机（速度 {_speed:F0} m/s 低于阈值）"; return; }
        if (!_sideLocked) { _status = "待机（通行方向标定中…）"; return; }

        TrafficVehicle? best = null;
        float bestAhead = float.MaxValue;
        foreach (var v in traffic.vehicles)
        {
            if (v == null || v.isTrailer || v.isTMP) continue;
            var rel = v.Position - pos;
            float ahead = Vector3.Dot(rel, _f);
            float lat = Vector3.Dot(rel, left);
            if (ahead < 4f || ahead > cfg.LookAhead) continue;
            if (Math.Abs(lat) > cfg.LaneBandHalf) continue;
            if (v.speed >= _speed - cfg.SpeedDiffThreshold) continue;
            if (ahead < bestAhead) { bestAhead = ahead; best = v; }
        }

        if (best == null)
        {
            _candidateId = -1;
            _candidateSeen = 0;
            _status = "待机（前方无慢车）";
            return;
        }

        if (best.id == _candidateId) _candidateSeen++;
        else { _candidateId = best.id; _candidateSeen = 1; }

        if (_candidateSeen < 4)
        {
            _status = $"发现慢车（确认中 {_candidateSeen}/4）";
            return;
        }

        float band = _passingSign * cfg.LaneOffset;
        if (CheckDanger(cfg, traffic, pos, left, band, strictAhead: true))
        {
            _status = $"发现慢车，但邻道受阻：{_dangerReason}";
            return;
        }

        _targetId = best.id;
        _targetDist = bestAhead;
        _targetSpeed = best.speed;
        _targetMissing = 0f;
        _targetFaster = 0f;
        _startPos = pos;
        _lateral = 0f;
        _prevLat = 0f;
        if (_passingSign > 0) _blinkL = true; else _blinkR = true;
        SetState(OState.Signaling, "打灯确认中");
    }

    private void TickSignaling(OvertakeSettings cfg, GameTelemetryData td, TrafficData traffic, Vector3 pos, Vector3 left, float dt)
    {
        _stateTime += dt;

        float band = _passingSign * cfg.LaneOffset;
        if (CheckDanger(cfg, traffic, pos, left, band, strictAhead: true))
        {
            Abort("邻道受阻：" + _dangerReason, cfg);
            return;
        }

        var t = FindVehicle(traffic, _targetId);
        if (t == null) { Abort("前车消失", cfg); return; }
        float ahead = Vector3.Dot(t.Position - pos, _f);
        if (ahead < 2f || ahead > cfg.LookAhead + 20f) { Abort("前车已离开检测范围", cfg); return; }
        if (t.speed > _speed - 0.5f) { Abort("前车加速", cfg); return; }
        _targetDist = ahead;
        _targetSpeed = t.speed;

        _steer = 0f;
        _throttle = 0f;
        _brake = 0f;
        _status = $"打灯确认中（{_stateTime:F1}/{cfg.ConfirmSeconds:F1} s）";

        if (_stateTime >= cfg.ConfirmSeconds)
        {
            SetState(OState.MovingOut, "变道中");
        }
    }

    private void TickMovingOut(OvertakeSettings cfg, GameTelemetryData td, TrafficData traffic, Vector3 pos, Vector3 left, float dt)
    {
        _stateTime += dt;
        float band = _passingSign * cfg.LaneOffset;

        if (CheckDanger(cfg, traffic, pos, left, band, strictAhead: true))
        {
            if (Math.Abs(_lateral) < 1.1f)
            {
                Abort("邻道受阻：" + _dangerReason, cfg);
                return;
            }
            // 已跨过车道线：返回原车道并减速
            StartReturning("邻道受阻，返回原车道");
            _steer = 0f;
            _throttle = 0f;
            _brake = ComputeBrake(cfg);
            return;
        }

        float dLat = (_lateral - _prevLat) * TickRate;
        float err = band - _lateral;
        float pid = cfg.SteeringGain * err - cfg.SteeringDamp * dLat;
        _steer = Math.Clamp(-_steerSignRight * pid, -cfg.MaxSteering, cfg.MaxSteering);
        CalibrateSteerSign(err, dLat);
        _throttle = ComputeThrottle(cfg, td);
        _brake = 0f;
        _status = $"变道中（侧向 {_lateral:F1} m）";

        if (Math.Abs(err) < 0.45f)
        {
            _blinkL = _blinkR = false;
            SetState(OState.Passing, "超车中");
        }
    }

    private void TickPassing(OvertakeSettings cfg, GameTelemetryData td, TrafficData traffic, Vector3 pos, Vector3 left, float dt)
    {
        _stateTime += dt;
        float band = _passingSign * cfg.LaneOffset;

        var t = FindVehicle(traffic, _targetId);
        if (t == null)
        {
            _targetMissing += dt;
            if (_targetMissing > 1.5f) { StartReturning("前车消失，返回"); return; }
        }
        else
        {
            _targetMissing = 0f;
            _targetDist = Vector3.Dot(t.Position - pos, _f);
            _targetSpeed = t.speed;
            if (_targetDist < -cfg.ReturnGap) { StartReturning("已超过，返回"); return; }
            if (t.speed > _speed + 1.5f)
            {
                _targetFaster += dt;
                if (_targetFaster > 3f) { StartReturning("前车加速，放弃"); return; }
            }
            else
            {
                _targetFaster = 0f;
            }
        }

        if (CheckDanger(cfg, traffic, pos, left, band, strictAhead: true))
        {
            _steer = 0f;
            _throttle = 0f;
            _brake = ComputeBrake(cfg);
            _status = $"超车中：{_dangerReason}，减速等待";
            return;
        }

        float dLat = (_lateral - _prevLat) * TickRate;
        float err = band - _lateral;
        float pid = cfg.SteeringGain * err - cfg.SteeringDamp * dLat;
        _steer = Math.Clamp(-_steerSignRight * pid, -cfg.MaxSteering, cfg.MaxSteering);
        CalibrateSteerSign(err, dLat);
        _throttle = ComputeThrottle(cfg, td);
        _brake = 0f;
        _status = $"超车中（前车 {_targetDist:F0} m）";
    }

    private void TickReturning(OvertakeSettings cfg, GameTelemetryData td, TrafficData traffic, Vector3 pos, Vector3 left, float dt)
    {
        _stateTime += dt;

        if (CheckDanger(cfg, traffic, pos, left, 0f, strictAhead: false))
        {
            _steer = 0f;
            _throttle = 0f;
            _brake = ComputeBrake(cfg);
            _status = $"返回中：{_dangerReason}，等待机会";
            return;
        }

        float dLat = (_lateral - _prevLat) * TickRate;
        float err = -_lateral;
        float pid = cfg.SteeringGain * err - cfg.SteeringDamp * dLat;
        _steer = Math.Clamp(-_steerSignRight * pid, -cfg.MaxSteering, cfg.MaxSteering);
        CalibrateSteerSign(err, dLat);
        _throttle = ComputeThrottle(cfg, td);
        _brake = 0f;
        _status = $"返回原车道（侧向 {_lateral:F1} m）";

        if (Math.Abs(_lateral) < 0.4f || _stateTime > 10f)
        {
            _blinkL = _blinkR = false;
            ReleaseOutput();
            SetIdle("完成 ✓");
            _cooldownUntil = _now + cfg.Cooldown;
        }
    }

    // ---------- 工具 ----------

    private void UpdateVehicleDirections(TrafficData traffic)
    {
        foreach (var v in traffic.vehicles)
        {
            if (v == null) continue;
            if (_prevVehPos.TryGetValue(v.id, out var prev))
            {
                var d = v.Position - prev;
                if (d.LengthSquared() > 0.05f)
                    _vehDir[v.id] = Vector3.Normalize(d);
            }
            _prevVehPos[v.id] = v.Position;
        }
    }

    private void CalibratePassingSide(OvertakeSettings cfg, TrafficData traffic, Vector3 pos, Vector3 left)
    {
        if (_sideLocked) return;
        if (cfg.PassingSide != 0)
        {
            _passingSign = Math.Sign(cfg.PassingSide);
            _sideLocked = true;
            return;
        }
        foreach (var v in traffic.vehicles)
        {
            if (v == null || v.isTMP || v.isTrailer) continue;
            if (!_vehDir.TryGetValue(v.id, out var dir)) continue;
            if (Vector3.Dot(dir, _f) > -0.4f) continue; // 只要对向来车
            float lat = Vector3.Dot(v.Position - pos, left);
            if (Math.Abs(lat) < 1.5f || Math.Abs(lat) > 15f) continue;
            _oncomingSignSum += Math.Sign(lat);
            _oncomingCount++;
        }
        if (_oncomingCount >= 12)
        {
            _passingSign = _oncomingSignSum >= 0 ? 1 : -1;
            _sideLocked = true;
            Logger.Info($"AutoOvertake: 通行方向标定完成 -> {(_passingSign > 0 ? "左侧超车" : "右侧超车")}（对向样本 {_oncomingCount}）");
        }
    }

    private void CalibrateSteerSign(float err, float dLat)
    {
        if (_steerSignLocked) return;
        if (Math.Abs(_steer) < 0.15f || Math.Abs(err) < 0.2f) return;
        if (Math.Abs(dLat) < 0.02f) return; // 侧向速度太慢，噪声大
        float expected = -_steerSignRight * _steer;
        _steerCorrCount++;
        if (Math.Sign(dLat) != Math.Sign(expected)) _steerCorrBad++;
        if (_steerCorrCount >= 25)
        {
            if (_steerCorrBad >= _steerCorrCount * 0.7f)
            {
                _steerSignRight = -_steerSignRight;
                Logger.Warn("AutoOvertake: 转向方向已自动校准（翻转 steerSignRight）");
            }
            _steerSignLocked = true;
        }
    }

    private bool CheckDanger(OvertakeSettings cfg, TrafficData traffic, Vector3 pos, Vector3 left, float bandCenter, bool strictAhead)
    {
        _danger = false;
        _dangerReason = "";
        _dangerVehSpeed = 0f;
        float bandHalf = cfg.LaneBandHalf + 0.3f;

        foreach (var v in traffic.vehicles)
        {
            if (v == null || v.isTrailer) continue;
            var rel = v.Position - pos;
            float lat = Vector3.Dot(rel, left);
            float ahead = Vector3.Dot(rel, _f);
            if (Math.Abs(lat - bandCenter) > bandHalf) continue;

            bool oncoming = !v.isTMP && _vehDir.TryGetValue(v.id, out var dir) && Vector3.Dot(dir, _f) < -0.3f;
            if (oncoming && ahead > 0f && ahead < cfg.OncomingRange)
            {
                _danger = true;
                _dangerReason = $"对向车 {ahead:F0} m";
                _dangerVehSpeed = v.speed;
                return true;
            }

            float aheadLimit = strictAhead ? cfg.DangerAhead : 12f;
            if (ahead > 0f && ahead < aheadLimit)
            {
                _danger = true;
                _dangerReason = $"邻道前方 {ahead:F0} m";
                _dangerVehSpeed = v.speed;
                return true;
            }

            if (ahead < 0f && ahead > -12f && v.speed > _speed * 0.6f)
            {
                _danger = true;
                _dangerReason = $"后方太近 {-ahead:F0} m";
                _dangerVehSpeed = v.speed;
                return true;
            }

            if (ahead < 0f && ahead > -cfg.DangerBehind && v.speed > _speed + 1.5f)
            {
                _danger = true;
                _dangerReason = $"后方快速接近 {-ahead:F0} m";
                _dangerVehSpeed = v.speed;
                return true;
            }
        }
        return false;
    }

    private float ComputeThrottle(OvertakeSettings cfg, GameTelemetryData td)
    {
        float desired = cfg.MaxOvertakeSpeed;
        float limit = td.truckFloat.speedLimit;
        if (cfg.RespectSpeedLimit && limit > 1f)
            desired = Math.Min(desired, limit + cfg.SpeedLimitExcess);
        if (_speed >= desired - 0.3f) return 0f;
        return Math.Clamp((desired - _speed) * cfg.ThrottleGain, 0f, 1f);
    }

    private float ComputeBrake(OvertakeSettings cfg)
    {
        float target = Math.Max(0f, _dangerVehSpeed - 2f);
        return Math.Clamp((_speed - target) * cfg.BrakeGain, 0f, 1f);
    }

    private static TrafficVehicle? FindVehicle(TrafficData traffic, short id)
    {
        foreach (var v in traffic.vehicles)
            if (v != null && v.id == id)
                return v;
        return null;
    }

    private void StartReturning(string status)
    {
        SetState(OState.Returning, status);
        if (_passingSign > 0) { _blinkL = false; _blinkR = true; }
        else { _blinkL = true; _blinkR = false; }
    }

    private void Abort(string reason, OvertakeSettings cfg)
    {
        ReleaseOutput();
        _targetId = -1;
        _candidateId = -1;
        _candidateSeen = 0;
        SetIdle("已中止：" + reason);
        _cooldownUntil = _now + cfg.AbortCooldown;
    }

    private void SetIdle(string status)
    {
        _state = OState.Idle;
        _stateTime = 0f;
        _status = status;
    }

    private void SetState(OState s, string status)
    {
        _state = s;
        _stateTime = 0f;
        _status = status;
    }

    // ---------- 输出 ----------

    private void PublishOutput()
    {
        _active = true;
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = ChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 8f, BooleanType = ControlBooleanType.Direct },
            Variables = new ControlVariables
            {
                steering = _steer,
                aforward = _throttle,
                abackward = _brake > 0f ? -_brake : 0f,
                lblinker = _blinkL,
                rblinker = _blinkR
            }
        });
    }

    private void ReleaseOutput()
    {
        if (!_active) return;
        _active = false;
        _steer = 0f;
        _throttle = 0f;
        _brake = 0f;
        _blinkL = false;
        _blinkR = false;

        // 1) 先发全零值，防止旧版 mmap 中布尔量残留（转向灯卡住）
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = ChannelId, Timeout = 0.5f },
            Properties = new ControlProperties { Weight = 8f, BooleanType = ControlBooleanType.Direct },
            Variables = new ControlVariables
            {
                steering = 0f,
                aforward = 0f,
                abackward = 0f,
                lblinker = false,
                rblinker = false
            }
        });
        // 2) 再删除通道
        Events.Current.Publish(OutputTopic, new ControlEvent
        {
            ChannelDefinition = new ControlChannelDefinition { Id = ChannelId },
            Variables = null!
        });
    }

    // ---------- UI ----------

    private void RenderUI()
    {
        var cfg = _cfg;
        ImGui.Text($"状态：{_status}");
        ImGui.Text($"速度 {_speed:F1} m/s　侧向 {_lateral:F2} m");
        ImGui.Text($"通行方向：{(_sideLocked ? (_passingSign > 0 ? "左侧超车" : "右侧超车") : "标定中…")}");
        if (_state != OState.Idle && _targetId >= 0)
            ImGui.Text($"目标车 #{_targetId}　距离 {_targetDist:F0} m　速度 {_targetSpeed:F1} m/s");
        if (_danger)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), $"⚠ {_dangerReason}");
        ImGui.Separator();

        bool dirty = false;
        dirty |= ImGui.Checkbox("启用自动超车", ref cfg.Enabled);
        dirty |= ImGui.SliderFloat("最小速度 (m/s)", ref cfg.MinSpeed, 8f, 25f);
        dirty |= ImGui.SliderFloat("超车速度上限 (m/s)", ref cfg.MaxOvertakeSpeed, 20f, 45f);
        dirty |= ImGui.SliderFloat("速度差阈值 (m/s)", ref cfg.SpeedDiffThreshold, 0.5f, 5f);
        dirty |= ImGui.SliderFloat("前车检测距离 (m)", ref cfg.LookAhead, 40f, 150f);
        dirty |= ImGui.SliderFloat("邻道预警距离 (m)", ref cfg.DangerAhead, 20f, 100f);
        dirty |= ImGui.SliderFloat("返回车距 (m)", ref cfg.ReturnGap, 15f, 60f);
        dirty |= ImGui.SliderFloat("车道侧向偏移 (m)", ref cfg.LaneOffset, 2.8f, 4.5f);
        dirty |= ImGui.SliderFloat("转向增益", ref cfg.SteeringGain, 0.01f, 0.08f);
        dirty |= ImGui.SliderFloat("转向阻尼", ref cfg.SteeringDamp, 0.05f, 0.8f);
        dirty |= ImGui.SliderFloat("最大转向", ref cfg.MaxSteering, 0.2f, 0.7f);
        dirty |= ImGui.SliderInt("通行方向 (-1右/0自动/+1左)", ref cfg.PassingSide, -1, 1);
        ImGui.Separator();

        if (ImGui.Button("立即中止并返回"))
        {
            if (_state is OState.Signaling or OState.MovingOut or OState.Passing)
                StartReturning("手动中止，返回原车道");
            else if (_state == OState.Idle)
                _status = "当前待机，无需中止";
        }

        if (dirty)
            _settingsDirty = true;
    }
}
