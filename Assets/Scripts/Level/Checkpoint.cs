using UnityEngine;

/// <summary>
/// 检查点。玩家碰到就把它设成新的复活点。
///
/// 长关卡必须有这个 —— 否则在 Boss 门口死了要从关卡最开头重跑 7000 像素。
///
/// 用法：在关卡里隔 2~3 屏放一个（TutorialLevelBuilder 会自动摆）。
/// 它自己会建一个柱状视觉，激活后变亮。
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class Checkpoint : MonoBehaviour
{
    [Tooltip("被激活之后的颜色")]
    public Color activeColor = new Color(0.4f, 1f, 0.6f, 1f);

    [Tooltip("没激活时的颜色")]
    public Color idleColor = new Color(0.55f, 0.6f, 0.72f, 1f);

    [Tooltip("复活点相对于检查点自身的偏移（像素）。默认放在柱子上方一点")]
    public Vector2 respawnOffset = new Vector2(0f, 16f);

    public bool IsActivated { get; private set; }

    /// <summary>所有检查点里当前生效的那个</summary>
    public static Checkpoint Current { get; private set; }

    /// <summary>清空"当前检查点"。重开一局时要调，否则会从上一局的检查点复活。</summary>
    public static void ClearCurrent()
    {
        Current = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        ClearCurrent();
    }

    SpriteRenderer _sprite;

    void Awake()
    {
        _sprite = GetComponentInChildren<SpriteRenderer>();

        var col = GetComponent<Collider2D>();
        if (col != null) col.isTrigger = true;

        Paint();
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other == null) return;
        if (other.GetComponentInParent<PlayerController>() == null) return;
        if (IsActivated) return;

        Activate();
    }

    void Activate()
    {
        IsActivated = true;
        Current = this;
        Paint();

        AudioManager.PlayFlat("sfx_checkpoint");

        Debug.Log(string.Format("[Checkpoint:{0}] 已激活，复活点更新到 {1}",
                  name, transform.position * PlayerController.PixelsPerUnit), this);
    }

    void Paint()
    {
        if (_sprite != null) _sprite.color = IsActivated ? activeColor : idleColor;
    }

    /// <summary>复活点（世界坐标）。没有激活过任何检查点就返回 null。</summary>
    public static Vector3? RespawnWorldPosition()
    {
        if (Current == null) return null;
        const float ppu = PlayerController.PixelsPerUnit;
        return Current.transform.position + new Vector3(
            Current.respawnOffset.x / ppu, Current.respawnOffset.y / ppu, 0f);
    }
}
