using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// 危险区：碰到就扣玩家的血。用来做尖刺、岩浆、带电地板这类地形伤害。
///
/// 用法（以尖刺 Tilemap 为例）：
///   1. 单独建一个 Tilemap（例如 "Hazard"），把尖刺 Tile 画上去
///   2. 给它加 TilemapCollider2D，**勾上 Is Trigger**（这一步最容易漏！）
///   3. 加这个脚本
///
/// 它和敌人的接触伤害走的是同一条路（PlayerController.TakeDamage），
/// 所以玩家的无敌帧、击退、受击闪烁都会自动生效，不用重复实现。
///
/// 【为什么不生效很难查】上面三步里任何一步没做，表现都是"尖刺不掉血"，
/// 而且不报错、不崩溃。所以 Start() 里会把这三条全部自检一遍并直接说清楚。
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class HazardZone : MonoBehaviour
{
    [Tooltip("碰到扣多少血")]
    public int damage = 1;

    [Tooltip("两次伤害之间的间隔（秒）。玩家自己的无敌帧才是主保险，这个只用来兜底")]
    public float damageInterval = 0.5f;

    [Tooltip("只对这些层生效。默认全部；一般不用改，因为只有玩家身上有 PlayerController")]
    public LayerMask targetLayers = ~0;

    float _cooldown;
    float _missLogTimer;
    int _baseDamage;
    bool _baseRecorded;

    /// <summary>记住基准伤害。难度系统切换时要按基准重算，不能在上一档的结果上再乘。</summary>
    public void RecordBaseStats()
    {
        _baseRecorded = true;
        _baseDamage = damage;
    }

    /// <summary>按难度重新配置尖刺伤害</summary>
    public void ApplyDifficulty(int newDamage)
    {
        if (!_baseRecorded) RecordBaseStats();
        damage = newDamage;
    }

    void Start()
    {
        SelfCheck();
    }

    /// <summary>
    /// 启动时把"尖刺不掉血"的三个常见原因全查一遍，直接说清楚是哪一条。
    /// </summary>
    void SelfCheck()
    {
        Collider2D col = GetComponent<Collider2D>();

        if (col == null)
        {
            Debug.LogError("[HazardZone] 这个物体上没有 Collider2D，尖刺不会造成任何伤害。\n" +
                           "修法：给它加 Tilemap Collider 2D，并勾上 Is Trigger。", this);
            return;
        }

        if (!col.isTrigger)
        {
            Debug.LogError("[HazardZone] 碰撞体没有勾 Is Trigger —— 这样它是一堵【实心墙】，" +
                           "玩家只会站在尖刺上面，OnTriggerStay2D 永远不会被调用。\n" +
                           "修法：选中这个物体 → Tilemap Collider 2D → 勾上 Is Trigger。", this);
            return;
        }

        // 如果是 Tilemap，顺便数一下这层上到底有多少格、其中多少格有碰撞体
        Tilemap tm = GetComponent<Tilemap>();
        if (tm == null)
        {
            Debug.Log("[HazardZone] 配置正常（Trigger 已勾）。这个物体不是 Tilemap，跳过格子统计。", this);
            return;
        }

        int total = 0;
        int noCollider = 0;
        int spriteCollider = 0;
        int gridCollider = 0;

        foreach (Vector3Int pos in tm.cellBounds.allPositionsWithin)
        {
            if (!tm.HasTile(pos)) continue;
            total++;

            Tile t = tm.GetTile<Tile>(pos);
            if (t == null) continue;

            if (t.colliderType == Tile.ColliderType.None) noCollider++;
            else if (t.colliderType == Tile.ColliderType.Sprite) spriteCollider++;
            else gridCollider++;
        }

        if (total == 0)
        {
            Debug.LogError("[HazardZone] 配置正常（Trigger 已勾），但【这一层上一格 Tile 都没有】—— " +
                           "尖刺多半画到别的 Tilemap 上去了（比如 Ground 层）。\n" +
                           "画在 Ground 层上的尖刺是实心的：玩家会站在上面，而且不会有任何伤害。", this);
            return;
        }

        if (noCollider == total)
        {
            Debug.LogError(string.Format(
                "[HazardZone] 这一层有 {0} 格，但【所有尖刺 Tile 的 Collider Type 都是 None】，不会生成碰撞体。\n" +
                "修法：在 Project 窗口里选中尖刺的 Tile 资源（Assets/Tiles/tile_spike_*），" +
                "把 Inspector 里的 Collider Type 改成 Sprite 或 Grid。", total), this);
            return;
        }

        Debug.Log(string.Format(
            "[HazardZone] 配置正常：Trigger 已勾，共 {0} 格（Sprite 碰撞 {1}、Grid 碰撞 {2}、无碰撞 {3}）。" +
            "站上去不掉血的话，看下面那条「碰到了但没扣到血」的警告。",
            total, spriteCollider, gridCollider, noCollider), this);

        // 自检之后把这块区域的碰撞体重新生成一次。
        // 运行时改过 Tile 的 Collider Type 时，Tilemap 的碰撞体不会自动刷新，
        // 常见现象就是"设置都对了，但就是没伤害"。
        if (col is TilemapCollider2D)
        {
            col.enabled = false;
            col.enabled = true;
        }
    }

    void Update()
    {
        if (_cooldown > 0f) _cooldown = Mathf.Max(_cooldown - Time.deltaTime, 0f);
        if (_missLogTimer > 0f) _missLogTimer = Mathf.Max(_missLogTimer - Time.deltaTime, 0f);
    }

    void OnTriggerStay2D(Collider2D other)
    {
        if (other == null) return;

        // 层过滤
        if ((targetLayers.value & (1 << other.gameObject.layer)) == 0) return;

        var player = other.GetComponentInParent<PlayerController>();
        if (player == null) return;

        if (_cooldown > 0f) return;

        if (player.TakeDamage(damage, transform.position))
        {
            _cooldown = damageInterval;
            return;
        }

        // 碰到了却没扣血：把原因打出来（每秒最多一条）
        if (_missLogTimer > 0f) return;
        _missLogTimer = 1f;

        Health h = player.GetComponent<Health>();
        Debug.LogWarning(string.Format(
            "[HazardZone:{0}] 扎到玩家了，但没扣到血。玩家无敌中 = {1}（受伤无敌剩余 {2:0.00} 秒），血量 = {3}",
            name, h != null && h.IsInvincible, h != null ? h.InvincibleTimer : -1f,
            h != null ? h.currentHealth : -1), this);
    }
}
