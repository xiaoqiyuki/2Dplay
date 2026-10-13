using UnityEngine;

/// <summary>
/// 玩家的远程弹道。朝面朝方向飞，打到第一个有 Health 的东西就结算，撞墙或超时消失。
///
/// 和 Shockwave 是同一套骨架（Kinematic 刚体 + Trigger + 手动画位置），
/// 那两个都验证过，不再另起炉灶。
///
/// 【为什么不做成受重力影响的抛物线】这个游戏的地面起伏很大（-5 行到 1 行），
/// 抛物线弹道会变成"要么打不到、要么只能打特定高度"，读不出手感。
/// 水平直飞的好处是"看得见就能打到"，玩家一眼能判断该不该按。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class PlayerProjectile : MonoBehaviour
{
    [Tooltip("飞行速度（像素/秒）。要明显快过敌人（最快 80），慢吞吞的弹道会被打断")]
    public float speed = 330f;

    public int damage = 1;

    [Tooltip("最长存活时间（秒）")]
    public float lifetime = 1.6f;

    [Tooltip("+1 = 向右，-1 = 向左")]
    public int direction = 1;

    [Tooltip("探测前方墙体的距离（像素）")]
    public float wallCheckDistance = 8f;

    [Tooltip("打到东西之后还能活多久（做击中特效的时间）")]
    public float lingerAfterHit = 0.04f;

    Rigidbody2D _rb;
    Collider2D _col;
    float _life;
    bool _hit;
    float _hitTimer;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _col = GetComponent<Collider2D>();

        _rb.bodyType = RigidbodyType2D.Kinematic;
        _rb.gravityScale = 0f;
        _col.isTrigger = true;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        _life += dt;

        if (_hit)
        {
            _hitTimer -= dt;
            if (_hitTimer <= 0f) Destroy(gameObject);
            return;
        }

        if (_life >= lifetime)
        {
            Destroy(gameObject);
            return;
        }

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

            // 角色不算墙（弹道应该能打到敌人，而不是被敌人"挡住"）
            if (c.GetComponentInParent<Health>() != null) continue;

            return true;
        }
        return false;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (_hit || other == null) return;

        // 别打自己人
        if (other.GetComponentInParent<PlayerController>() != null) return;

        Health target = other.GetComponentInParent<Health>();
        if (target == null || target.IsDead) return;

        _hit = true;
        _hitTimer = lingerAfterHit;

        target.TakeDamage(damage, transform.position);
        AudioManager.Play("sfx_hit", 0.7f);

        // 命中特效：复用尘埃系统，不新建
        SpawnImpact();
    }

    void SpawnImpact()
    {
        Sprite spark = Greybox.Solid(new Color(0.7f, 0.95f, 1f, 1f), 2, 2);

        for (int i = 0; i < 5; i++)
        {
            var go = new GameObject("BoltSpark");
            if (transform.parent != null) go.transform.SetParent(transform.parent, false);
            go.transform.position = transform.position;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = spark;
            sr.color = new Color(0.7f, 0.95f, 1f, 1f);
            sr.sortingOrder = 20;

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float spd = Random.Range(50f, 130f) / PlayerController.PixelsPerUnit;

            var puff = go.AddComponent<DustPuff>();
            puff.velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * spd;
            puff.gravity = -4f;
            puff.lifetime = 0.18f;
            puff.fadeStart = 0.2f;
        }
    }
}
