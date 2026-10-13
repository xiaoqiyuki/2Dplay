using System.Collections;
using UnityEngine;

/// <summary>
/// 死亡区：掉进来的玩家会被送回【最后一个激活过的检查点】，
/// 没有检查点时才回初始出生点。速度清零。
/// 挂在一个带 Trigger Collider2D 的物体上。
///
/// 会先冻结玩家一小段时间再传送 —— 让玩家意识到"我死了"，
/// 而不是画面莫名其妙跳回去了。这段时间可以在 Inspector 里调，设 0 就是瞬间重开。
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class KillZone : MonoBehaviour
{
    [Tooltip("要送回的目标。留空则自动找场景里的 PlayerController")]
    public Transform target;

    [Header("重开手感")]
    [Tooltip("死亡后停顿多久再回出生点（秒）。设 0 就是瞬间重开")]
    public float deathPause = 0.15f;

    Vector3 _spawnPosition;
    Rigidbody2D _targetBody;
    bool _respawning;

    void Start()
    {
        if (target == null)
        {
            var pc = FindFirstObjectByType<PlayerController>();
            if (pc != null)
            {
                target = pc.transform;
            }
        }

        if (target != null)
        {
            _spawnPosition = target.position;
            _targetBody = target.GetComponent<Rigidbody2D>();
        }
        else
        {
            Debug.LogWarning("[KillZone] 没找到玩家，死亡区不会生效。", this);
        }
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (target == null || _respawning) return;
        if (other.transform != target && !other.transform.IsChildOf(target)) return;
        StartCoroutine(RespawnRoutine());
    }

    IEnumerator RespawnRoutine()
    {
        _respawning = true;

        // 冻结物理，让玩家在原地停一下
        if (_targetBody != null) _targetBody.simulated = false;

        if (deathPause > 0f)
        {
            yield return new WaitForSeconds(deathPause);
        }

        DoRespawn();

        if (_targetBody != null) _targetBody.simulated = true;
        _respawning = false;
    }

    /// <summary>
    /// 立刻把玩家送回出生点（不经过停顿）。
    /// 绑到按钮上可以做「重开」，TestLevelBootstrap 里的 R 键走的就是这里。
    /// </summary>
    public void Respawn()
    {
        DoRespawn();
    }

    void DoRespawn()
    {
        if (target == null) return;

        // ⚠️ 优先用【激活过的检查点】，没有才回初始出生点。
        //
        // 原来这里固定用 Start() 时记下的 _spawnPosition —— 也就是"开局站的位置"，
        // 之后就再也不改了。后果是：
        //   · 掉进死亡区 → 永远回关卡最开头，检查点形同虚设
        //   · 按 R        → 走的是下面那个 Respawn()，同样回关卡最开头
        // 表现就是"检查点只在第一次死亡时有用"——其实它一次都没用过，
        // 第一次之所以看起来对，只是因为那时玩家还没走远。
        Vector3 dst = _spawnPosition;
        Vector3? cp = Checkpoint.RespawnWorldPosition();
        if (cp.HasValue) dst = cp.Value;

        if (_targetBody != null)
        {
            _targetBody.linearVelocity = Vector2.zero;
            _targetBody.angularVelocity = 0f;
            _targetBody.position = new Vector2(dst.x, dst.y);
        }
        target.position = dst;

        Debug.Log(string.Format("[KillZone] 已送回：{0}（{1}）",
                  dst, cp.HasValue ? "最后一个检查点" : "初始出生点"), this);
    }
}
