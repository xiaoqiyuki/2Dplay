using UnityEngine;

/// <summary>
/// Boss 落地时向两侧扩散的冲击波。碰到玩家扣血，撞到墙或超时就消失。
///
/// 用 Kinematic 刚体 + Trigger：它不需要物理模拟，只需要"能触发 OnTrigger"。
/// 伤害走的是和敌人 / 尖刺同一条路（PlayerController.TakeDamage），
/// 所以玩家的无敌帧、击退、受击闪烁全都自动生效。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class Shockwave : MonoBehaviour
{
    [Tooltip("扩散速度（像素/秒）。要比玩家跑速（140）慢，否则躲不掉")]
    public float speed = 150f;

    [Tooltip("+1 = 向右，-1 = 向左")]
    public int direction = 1;

    [Tooltip("最长存活时间（秒）")]
    public float lifetime = 2.4f;

    public int damage = 1;

    [Tooltip("探测前方有没有墙的距离（像素）")]
    public float wallCheckDistance = 10f;

    Rigidbody2D _rb;
    Collider2D _col;
    float _life;
    float _hitCooldown;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _col = GetComponent<Collider2D>();

        _rb.bodyType = RigidbodyType2D.Kinematic;
        _rb.gravityScale = 0f;
        _col.isTrigger = true;

        // 两道冲击波同时生成，音量压一点、音高错开，听起来才像"两侧炸开"
        AudioManager.Play("sfx_shockwave", 0.45f, 0.12f);
    }

    void Update()
    {
        if (_hitCooldown > 0f) _hitCooldown = Mathf.Max(_hitCooldown - Time.deltaTime, 0f);
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        _life += dt;

        if (_life >= lifetime)
        {
            Destroy(gameObject);
            return;
        }

        // 前方有实心地形就消失（撞墙了）
        if (HasWallAhead())
        {
            Destroy(gameObject);
            return;
        }

        Vector2 p = _rb.position;
        p.x += direction * speed / PlayerController.PixelsPerUnit * dt;
        _rb.MovePosition(p);
    }

    bool HasWallAhead()
    {
        Bounds b = _col.bounds;
        Vector2 origin = new Vector2(b.center.x, b.center.y);
        float distance = b.extents.x + wallCheckDistance / PlayerController.PixelsPerUnit;

        RaycastHit2D[] hits = Physics2D.RaycastAll(origin, new Vector2(direction, 0f), distance);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D c = hits[i].collider;
            if (c == null || c.isTrigger) continue;
            if (c.transform == transform || c.transform.IsChildOf(transform)) continue;

            // 别的角色（有 Health 的）不算墙，冲击波要能穿过敌人
            if (c.GetComponentInParent<Health>() != null) continue;

            return true;
        }
        return false;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other == null || _hitCooldown > 0f) return;

        var player = other.GetComponentInParent<PlayerController>();
        if (player == null) return;

        // Boss 的冲击波也能被弹刀挡下来 —— 而且弹掉它是【直接消散】，
        // 给玩家一个"看准时机清场"的高光时刻。这是"弹刀值得练"的理由之一。
        PlayerCombat combat = player.GetComponent<PlayerCombat>();
        if (combat != null && combat.TryParry())
        {
            AudioManager.PlayFlat("sfx_parry", 0.8f);
            Destroy(gameObject);
            return;
        }

        if (player.TakeDamage(damage, transform.position))
        {
            _hitCooldown = 0.8f;
        }
    }
}
