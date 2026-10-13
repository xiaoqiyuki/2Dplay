using UnityEngine;

public enum Difficulty { Easy, Normal, Hard }

/// <summary>
/// 难度档位。三个档位改的是**同一批参数**，只是系数不同。
///
/// 【为什么是"回头重新配置"而不是"生成时按难度生成"】
/// 关卡是在 TestLevelBootstrap.Awake() 里一次性生成的，而难度是玩家在主菜单里选的 ——
/// 那时候敌人早就建好了。所以要反过来：让每个敌人先记住自己的【基准数值】，
/// 选定难度之后由这里统一重新算一遍。见 EnemyPatrol.RecordBaseStats / ApplyDifficulty。
///
/// 这样也带来一个好处：以后做"游戏中途改难度"（很多游戏都有）不需要改架构。
/// </summary>
public static class DifficultySettings
{
    public struct Profile
    {
        public string Name;
        public string Description;

        public int PlayerHealth;
        public float PlayerInvincibleTime;

        /// <summary>远程弹道的一次性备弹量。越简单给得越多，和其他缩放方向一致。</summary>
        public int RangedAmmo;

        public float EnemyHealthMul;
        public float EnemySpeedMul;
        public int EnemyContactDamage;

        public int SpikeDamage;

        public int BossHealth;
        public float BossSlamIntervalMul;
        public int BossContactDamage;
        public int BossShockwaveDamage;
    }

    public static Profile Get(Difficulty d)
    {
        switch (d)
        {
            case Difficulty.Easy:
                return new Profile
                {
                    Name = "简单",
                    Description = "5 颗心 · 敌人更脆更慢 · 远程 6 发 · Boss 只有 14 血",
                    PlayerHealth = 5,
                    PlayerInvincibleTime = 1.1f,
                    RangedAmmo = 6,
                    EnemyHealthMul = 0.7f,
                    EnemySpeedMul = 0.85f,
                    EnemyContactDamage = 1,
                    SpikeDamage = 1,
                    BossHealth = 14,
                    BossSlamIntervalMul = 1.35f,     // 出招更慢
                    BossContactDamage = 1,
                    BossShockwaveDamage = 1,
                };

            case Difficulty.Hard:
                return new Profile
                {
                    Name = "困难",
                    Description = "3 颗心 · 敌人更硬更快 · 远程只有 3 发 · Boss 30 血",
                    PlayerHealth = 3,
                    PlayerInvincibleTime = 0.5f,
                    RangedAmmo = 3,
                    // 注意：敌人伤害翻倍 + 无敌帧只有 0.5 秒，
                    // 被两个敌人夹住会掉血非常快 —— 这是故意的
                    EnemyHealthMul = 1.5f,
                    EnemySpeedMul = 1.15f,
                    EnemyContactDamage = 2,
                    SpikeDamage = 2,
                    BossHealth = 30,
                    BossSlamIntervalMul = 0.72f,
                    BossContactDamage = 2,
                    BossShockwaveDamage = 2,
                };

            default:
                return new Profile
                {
                    Name = "一般",
                    Description = "4 颗心 · 标准强度 · 远程 4 发 · Boss 20 血",
                    PlayerHealth = 4,
                    PlayerInvincibleTime = 0.8f,
                    RangedAmmo = 4,
                    EnemyHealthMul = 1f,
                    EnemySpeedMul = 1f,
                    EnemyContactDamage = 1,
                    SpikeDamage = 1,
                    BossHealth = 20,
                    BossSlamIntervalMul = 1.15f,     // 比原来 3.2 秒的间隔再松一点
                    BossContactDamage = 2,
                    BossShockwaveDamage = 1,
                };
        }
    }

    /// <summary>当前档位。Boss 召唤的小兵要用它 —— 那些小兵是在 Apply() 之后才生成的。</summary>
    public static Difficulty Current { get; private set; } = Difficulty.Normal;

    /// <summary>
    /// 把当前难度单独应用到一个敌人上。
    /// 给"运行中途新生成的敌人"用 —— 目前就是 Boss 二阶段召唤的小兵。
    /// 它们出生在 Apply() 之后，如果不补这一下，就会用出厂数值（简单难度下等于没降）。
    /// </summary>
    public static void ApplyTo(EnemyPatrol e)
    {
        if (e == null) return;
        Profile p = Get(Current);
        e.ApplyDifficulty(p.EnemyHealthMul, p.EnemySpeedMul, p.EnemyContactDamage);
    }

    /// <summary>
    /// 把难度应用到场景里所有东西上。
    /// 在玩家点"开始游戏"之后调用 —— 那时场景已经建好了。
    /// </summary>
    public static void Apply(Difficulty d)
    {
        Current = d;
        Profile p = Get(d);

        // ---- 玩家 ----
        PlayerController player = Object.FindFirstObjectByType<PlayerController>();
        if (player != null)
        {
            Health h = player.GetComponent<Health>();
            if (h != null)
            {
                h.maxHealth = p.PlayerHealth;
                h.currentHealth = p.PlayerHealth;
                h.invincibleTime = p.PlayerInvincibleTime;
            }

            // 远程备弹量也跟着难度走（简单 6 / 一般 4 / 困难 3）
            player.ApplyDifficultyAmmo(p.RangedAmmo);
        }

        // ---- 敌人 ----
        int enemyCount = 0;
        EnemyPatrol[] enemies = Object.FindObjectsByType<EnemyPatrol>(FindObjectsSortMode.None);
        for (int i = 0; i < enemies.Length; i++)
        {
            if (enemies[i] == null) continue;
            enemies[i].ApplyDifficulty(p.EnemyHealthMul, p.EnemySpeedMul, p.EnemyContactDamage);
            enemyCount++;
        }

        // ---- Boss ----
        BossController boss = Object.FindFirstObjectByType<BossController>();
        if (boss != null)
        {
            boss.ApplyDifficulty(p.BossHealth, p.BossSlamIntervalMul,
                                 p.BossContactDamage, p.BossShockwaveDamage);
        }

        // ---- 尖刺等危险区 ----
        HazardZone[] hazards = Object.FindObjectsByType<HazardZone>(FindObjectsSortMode.None);
        for (int i = 0; i < hazards.Length; i++)
        {
            if (hazards[i] != null) hazards[i].ApplyDifficulty(p.SpikeDamage);
        }

        Debug.Log(string.Format(
            "[Difficulty] 已切换到【{0}】：玩家 {1} 心 / 无敌 {2:0.0} 秒，" +
            "敌人 ×{3:0.0} 血 ×{4:0.0} 速 伤害 {5}（{6} 个），尖刺伤害 {7}，Boss {8} 血",
            p.Name, p.PlayerHealth, p.PlayerInvincibleTime,
            p.EnemyHealthMul, p.EnemySpeedMul, p.EnemyContactDamage, enemyCount,
            p.SpikeDamage, boss != null ? p.BossHealth.ToString() : "无"));
    }
}
