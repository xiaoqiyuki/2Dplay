using UnityEngine;

/// <summary>
/// 调参用的调试面板。用 OnGUI 画，不需要 Canvas，挂到玩家身上即可。
///
/// 面板文字保持英文——Unity 的默认 GUI 字体不含中文字形，写中文会显示成方块。
/// 速度换算回了「像素/秒」，方便和脚本里的参数、以及制作方案文档里的表格直接对照。
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class DebugHud : MonoBehaviour
{
    public bool visible = true;

    [Tooltip("距离屏幕顶部的偏移。上面要留出心形血条的位置")]
    public float topOffset = 52f;

    PlayerController _player;
    Health _health;
    PlayerCombat _combat;
    GUIStyle _style;
    float _fps;

    void Awake()
    {
        _player = GetComponent<PlayerController>();
    }

    void Update()
    {
        float instant = 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f);
        _fps = Mathf.Lerp(_fps <= 0f ? instant : _fps, instant, 0.1f);
    }

    void OnGUI()
    {
        if (!visible || _player == null) return;

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _style.normal.textColor = new Color(1f, 0.95f, 0.6f);
        }

        Vector2 v = _player.Velocity * PlayerController.PixelsPerUnit;

        // Health 是运行时加上去的，所以这里懒加载
        if (_health == null) _health = GetComponent<Health>();
        string hp = _health != null
            ? string.Format("{0}/{1}{2}", _health.currentHealth, _health.maxHealth,
                            _health.IsInvincible ? "  INVUL" : "")
            : "--";

        // PlayerCombat 也是运行时加上去的，同样懒加载
        if (_combat == null) _combat = GetComponent<PlayerCombat>();
        string parry = "--";
        if (_combat != null)
        {
            parry = _combat.IsParryWindowActive ? "WINDOW OPEN" : "ready";
        }

        string text = string.Format(
            "FPS {0:0}\n" +
            "STATE {1}   {2}\n" +
            "VEL {3:0}, {4:0} px/s\n" +
            "COYOTE {5:0.00}   BUFFER {6:0.00}\n" +
            "AIRJUMP {7}   DASHCD {8:0.00}\n" +
            "LAST LAND {9:0} px/s\n" +
            "HP {10}\n" +
            "PARRY {11}",
            _fps,
            _player.CurrentState,
            _player.IsGrounded ? "GROUND" : "AIR",
            v.x, v.y,
            _player.CoyoteTimer,
            _player.JumpBufferTimer,
            _player.AirJumpsLeft,
            _player.DashCooldownTimer,
            _player.LastLandingSpeed,
            hp,
            parry);

        GUI.Label(new Rect(12f, topOffset, 480f, 175f), text, _style);
    }
}
