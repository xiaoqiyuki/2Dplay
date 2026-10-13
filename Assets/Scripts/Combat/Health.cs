using UnityEngine;

/// <summary>
/// 通用血量组件。玩家和敌人共用同一套。
///
/// 它只负责"扣血 / 无敌帧 / 播广播"，不负责击退、闪烁、死亡动画 ——
/// 那些交给各自的控制器去做，因为玩家和敌人的表现完全不同。
/// 这样拆的好处是：以后加血瓶、护盾、Boss 多段血条，改的都是这一个文件。
/// </summary>
public class Health : MonoBehaviour
{
    [Header("血量")]
    public int maxHealth = 3;
    public int currentHealth = 3;

    [Header("无敌帧")]
    [Tooltip("受伤后多久内免疫伤害（秒）。0 = 没有无敌帧，会被连续扣血")]
    public float invincibleTime = 0.8f;

    /// <summary>受伤时触发。参数：伤害值、伤害来源的世界坐标</summary>
    public event System.Action<int, Vector2> Damaged;

    /// <summary>血量归零时触发</summary>
    public event System.Action Died;

    public bool IsDead => currentHealth <= 0;

    /// <summary>是否免疫伤害（受伤无敌帧 + 外部授予的免疫，两者取或）</summary>
    public bool IsInvincible => _invincibleTimer > 0f || _externalInvincibleTimer > 0f;

    /// <summary>
    /// 是否处于"受伤之后的"无敌帧。
    /// 表现层（精灵闪红）只应该看这个 —— 冲刺和弹刀的无敌不该让角色闪红。
    /// </summary>
    public bool HasDamageInvincibility => _invincibleTimer > 0f;

    public float InvincibleTimer => _invincibleTimer;

    float _invincibleTimer;
    float _externalInvincibleTimer;

    void Update()
    {
        if (_invincibleTimer > 0f)
        {
            _invincibleTimer = Mathf.Max(_invincibleTimer - Time.deltaTime, 0f);
        }
        if (_externalInvincibleTimer > 0f)
        {
            _externalInvincibleTimer = Mathf.Max(_externalInvincibleTimer - Time.deltaTime, 0f);
        }
    }

    /// <summary>
    /// 从外部授予一段免疫时间。冲刺无敌帧（每帧刷新）、弹刀成功后的奖励无敌都用它。
    /// 和受伤无敌帧分开计时，互不干扰。
    /// </summary>
    public void GrantInvincibility(float duration)
    {
        if (duration <= 0f) return;
        _externalInvincibleTimer = Mathf.Max(_externalInvincibleTimer, duration);
    }

    /// <summary>
    /// 扣血。处于无敌帧、已经死了、或者伤害值小于等于 0 时会被直接拒绝。
    /// 返回 true 表示这次伤害真的生效了（用来决定要不要播打击反馈）。
    /// </summary>
    public bool TakeDamage(int amount, Vector2 sourcePosition)
    {
        if (amount <= 0 || IsDead || IsInvincible) return false;

        currentHealth = Mathf.Max(currentHealth - amount, 0);
        _invincibleTimer = invincibleTime;

        if (Damaged != null) Damaged(amount, sourcePosition);
        if (currentHealth <= 0 && Died != null) Died();

        return true;
    }

    /// <summary>回血，不会超过上限。</summary>
    public void Heal(int amount)
    {
        if (amount <= 0 || IsDead) return;
        currentHealth = Mathf.Min(currentHealth + amount, maxHealth);
    }

    /// <summary>满血复活并清空所有无敌计时，给"重开"用。</summary>
    public void ResetFull()
    {
        currentHealth = maxHealth;
        _invincibleTimer = 0f;
        _externalInvincibleTimer = 0f;
    }
}
