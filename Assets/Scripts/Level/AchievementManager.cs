using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 成就系统。五个成就，全部围绕"怎么通关"。
///
/// 【两个计数型成就不统计 Boss，也不统计 Boss 召唤的小兵】
///   · Boss 必须打死才能通关，"一个敌人不杀"如果把它算进去就永远拿不到。
///     所以"不杀 / 杀光"只看路上的杂兵 —— Boss 是必经流程，单独看它没有意义。
///   · 二阶段会无限召唤小兵，算进去的话"杀光"永远不可能达成。
///
/// 【"掉下去的不算"怎么实现】
/// 敌人有两种消失方式，走的是两条不同的代码路径：
///   · 血扣光        → EnemyPatrol.OnDied()          → 算"被玩家杀"
///   · 掉出关卡销毁  → EnemyPatrol 掉出阈值的分支     → 算"掉下去"，不算玩家杀的
/// 所以"杀光"的判据是：被玩家杀的 == 生成的 - 掉下去的，
/// 也就是"没掉下去的那些，全都得是玩家亲手杀的"。
/// </summary>
public class AchievementManager : MonoBehaviour
{
    public class Entry
    {
        public string Id;
        public string Title;
        public string Desc;
    }

    // 成就定义。顺序就是显示顺序。
    public static readonly Entry[] All =
    {
        new Entry
        {
            Id = "no_death",
            Title = "一命通关",
            Desc = "不死亡通关一次",
        },
        new Entry
        {
            Id = "pacifist",
            Title = "和平主义者",
            Desc = "不杀任何杂兵通关（掉下悬崖的不算）",
        },
        new Entry
        {
            Id = "hard_clear",
            Title = "硬核",
            Desc = "以【困难】难度通关",
        },
        new Entry
        {
            Id = "casual_clear",
            Title = "初次登顶",
            Desc = "以【简单】或【一般】难度通关",
        },
        new Entry
        {
            Id = "exterminator",
            Title = "清道夫",
            Desc = "杀光所有没掉下悬崖的杂兵并通关",
        },
    };

    const string KeyPrefix = "2Dplay_Ach_";

    public static AchievementManager Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
    }

    // ---- 本局统计 ----
    int _deaths;
    int _enemiesSpawned;          // 只算关卡生成时就在的杂兵
    int _enemiesKilledByPlayer;
    int _enemiesFell;

    public int Deaths { get { return _deaths; } }
    public int EnemiesSpawned { get { return _enemiesSpawned; } }
    public int EnemiesKilled { get { return _enemiesKilledByPlayer; } }
    public int EnemiesFell { get { return _enemiesFell; } }

    void Awake()
    {
        Instance = this;
    }

    // ---------------------------------------------------------------- 统计入口
    // 全部做成 static + 判空：调用方（敌人、Boss、流程）插一句就行，
    // 不会因为"场景里没挂管理器"而抛空引用。

    public static void NotifyEnemySpawned(EnemyPatrol e)
    {
        if (Instance == null || e == null) return;
        if (e.isSummoned) return;          // Boss 召唤的不算
        Instance._enemiesSpawned++;
    }

    public static void NotifyEnemyKilled(EnemyPatrol e)
    {
        if (Instance == null || e == null) return;
        if (e.isSummoned) return;
        Instance._enemiesKilledByPlayer++;
    }

    public static void NotifyEnemyFell(EnemyPatrol e)
    {
        if (Instance == null || e == null) return;
        if (e.isSummoned) return;
        Instance._enemiesFell++;
    }

    public static void NotifyPlayerDied()
    {
        if (Instance == null) return;
        Instance._deaths++;
    }

    public static void ResetRun()
    {
        if (Instance == null) return;
        Instance._deaths = 0;
        Instance._enemiesSpawned = 0;
        Instance._enemiesKilledByPlayer = 0;
        Instance._enemiesFell = 0;
    }

    // ---------------------------------------------------------------- 结算

    /// <summary>
    /// 通关时结算。返回【本次新解锁】的成就（已经拿过的不会重复返回）。
    /// </summary>
    public static List<Entry> EvaluateOnVictory(Difficulty difficulty)
    {
        var newly = new List<Entry>();
        if (Instance == null) return newly;

        int killed = Instance._enemiesKilledByPlayer;
        int fell = Instance._enemiesFell;
        int spawned = Instance._enemiesSpawned;

        // 没掉下去的那些，是不是全被玩家杀了
        int mustKill = Mathf.Max(0, spawned - fell);
        bool clearedAll = spawned > 0 && killed >= mustKill;

        TryUnlock("no_death", Instance._deaths == 0, newly);
        TryUnlock("pacifist", killed == 0, newly);
        TryUnlock("hard_clear", difficulty == Difficulty.Hard, newly);
        TryUnlock("casual_clear", difficulty == Difficulty.Easy || difficulty == Difficulty.Normal, newly);
        TryUnlock("exterminator", clearedAll, newly);

        Debug.Log(string.Format(
            "[成就] 结算：难度={0} 死亡={1} 杂兵 生成 {2} / 玩家杀 {3} / 掉下去 {4} → 需击杀 {5}，杀光={6}",
            difficulty, Instance._deaths, spawned, killed, fell, mustKill, clearedAll), Instance);

        return newly;
    }

    static void TryUnlock(string id, bool condition, List<Entry> newly)
    {
        if (!condition) return;

        // 条件成立且没解锁过 → 解锁
        if (PlayerPrefs.GetInt(KeyPrefix + id, 0) == 1) return;

        PlayerPrefs.SetInt(KeyPrefix + id, 1);
        PlayerPrefs.Save();

        Entry e = Find(id);
        if (e != null)
        {
            newly.Add(e);
            Debug.Log(string.Format("[成就] 解锁【{0}】—— {1}", e.Title, e.Desc), Instance);
        }
    }

    /// <summary>某个成就是否已经拿到（跨局持久）</summary>
    public static bool IsUnlocked(string id)
    {
        return PlayerPrefs.GetInt(KeyPrefix + id, 0) == 1;
    }

    /// <summary>已解锁数量 / 总数</summary>
    public static void GetProgress(out int unlocked, out int total)
    {
        total = All.Length;
        unlocked = 0;
        for (int i = 0; i < All.Length; i++)
        {
            if (IsUnlocked(All[i].Id)) unlocked++;
        }
    }

    public static Entry Find(string id)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (All[i].Id == id) return All[i];
        }
        return null;
    }
}
