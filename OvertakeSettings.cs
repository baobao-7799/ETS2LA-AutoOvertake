// ETS2LA Auto Overtake — automatic overtaking for ETS2 / ATS.
// Copyright (C) 2026 baobao-7799
// SPDX-License-Identifier: GPL-3.0-or-later
namespace FishLeia.AutoOvertake;

/// <summary>
/// 插件设置。保存于 %APPDATA%\ETS2LA\fishleia.autoovertake.json，
/// 由 ETS2LA 的 SettingsHandler 按公共字段（IncludeFields）序列化。
/// </summary>
public class OvertakeSettings
{
    // 总开关与窗口
    public bool Enabled = true;
    public bool ShowWindow = true;

    // 触发条件
    public float MinSpeed = 12f;             // m/s，低于该速度不超车
    public float MaxOvertakeSpeed = 33f;     // m/s，超车过程中的目标速度上限（~119 km/h）
    public float SpeedDiffThreshold = 1.2f;  // m/s，前车必须至少比我们慢这么多才触发
    public float LookAhead = 90f;            // m，前方慢车检测距离

    // 安全
    public float DangerAhead = 45f;          // m，邻道前方该范围内有车即视为阻挡
    public float DangerBehind = 30f;         // m，邻道后方有更快来车（或太近）即阻挡
    public float OncomingRange = 160f;       // m，对向车预警距离
    public float ReturnGap = 35f;            // m，超过前车多远后返回原车道
    public float LaneOffset = 3.5f;          // m，变道目标侧向偏移（车道宽）
    public float LaneBandHalf = 1.1f;        // m，车道带半宽

    // 控制
    public float SteeringGain = 0.035f;      // 侧向误差比例增益
    public float SteeringDamp = 0.35f;       // 侧向速度阻尼
    public float MaxSteering = 0.45f;        // 最大转向输出（0~1）
    public float ThrottleGain = 0.18f;       // 速度误差->油门增益
    public float BrakeGain = 0.3f;           // 速度误差->刹车增益
    public float ConfirmSeconds = 0.6f;      // s，打灯确认时长

    // 限速
    public bool RespectSpeedLimit = true;
    public float SpeedLimitExcess = 1.4f;    // m/s（约 5 km/h），允许超过限速的值

    // 冷却与通行方向
    public float Cooldown = 2.5f;            // s，完成一次超车后的冷却
    public float AbortCooldown = 3f;         // s，中止后的冷却
    public float UserCooldown = 4f;          // s，用户接管后的冷却
    public int PassingSide = 0;              // 0=自动标定（对向来车判断），+1=强制左侧超车，-1=强制右侧超车
}
