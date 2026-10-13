using System.Collections;
using UnityEngine;

/// <summary>
/// 地面巡逻兵。
///
/// 行为：
///   · 朝一个方向匀速走，**前方没有地面**或者**撞到墙**就掉头
///   · 接触玩家就扣血（靠挂在同一个物体上的 Trigger 碰撞体）
///   · 被玩家打中会短暂僵直 + 变白闪一下
///   · 血扣光就炸成几块方块
///
/// 关于"前方没有地面"的检测：在身体前缘往下打一条短射线。
/// 这是平台游戏里最常用的做法 —— 比"走到平台外再检测脚下"要稳得多，
/// 因为后者在高速移动时会直接掉下去。
///
/// 【为什么用 Unity 自带重力】敌人不像玩家那样需要非对称重力，用 gravityScale = 1
/// 让它自然落到地面上即可，省一堆代码。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Health))]
public class EnemyPatrol : MonoBehaviour
{
    public enum Mode { Patrol, Chase }

    /// <summary>
    /// 敌人分级。三种都共用同一套巡逻 / 追击 / 重力 / 探路代码，
    /// 只在"发现玩家之后怎么做"上分叉 —— 复用已经调好的运动逻辑，少踩坑。
    /// </summary>
    public enum Kind
    {
        Grunt,      // 普通兵：追上来撞你
        Brute,      // 重甲兵：血厚、慢、撞得疼，而且【打不退】
        Charger,    // 冲锋兵：蓄力一下，然后高速冲撞 —— 这是游戏里第一个"有前摇的攻击"
    }

    [Header("类型")]
    public Kind kind = Kind.Grunt;

    [Tooltip("是不是 Boss 召唤出来的。成就的\"不杀 / 杀光\"会排除这些")]
    [System.NonSerialized] public bool isSummoned;
    [Header("巡逻")]
    [Tooltip("移动速度（像素/秒）")]
    public float moveSpeed = 40f;
    [Tooltip("初始朝向：1 = 右，-1 = 左")]
    public int startDirection = -1;
    [Tooltip("检测前方有没有地面的距离（像素）")]
    public float edgeCheckDistance = 12f;
    [Tooltip("检测前方有没有墙的距离（像素）")]
    public float wallCheckDistance = 8f;

    [Header("重力（脚本自己施加，不用全局重力）")]
    [Tooltip("下落加速度（像素/秒²）")]
    public float gravity = 1400f;
    [Tooltip("最大下落速度（像素/秒）")]
    public float maxFallSpeed = 500f;

    [Header("追击 · 进入条件")]
    [Tooltip("玩家进入这个距离（像素）就开始追")]
    public float detectRange = 150f;
    [Tooltip("玩家和敌人的高度差超过这个值（像素）就不会【开始】追（比如玩家站在很高的平台上）")]
    public float detectHeightLimit = 100f;

    [Header("追击 · 脱离条件（故意比进入条件宽松，形成迟滞，避免在边界上反复横跳）")]
    [Tooltip("玩家超出这个距离（像素）才开始考虑放弃追击")]
    public float loseRange = 320f;
    [Tooltip("高度差超过这个值（像素）才放弃追击。必须比 Detect Height Limit 大不少 —— " +
             "玩家二段跳能跳 85 像素，如果拿 90 当上限，他随便跳一下敌人就脱锁了")]
    public float loseHeightLimit = 220f;
    [Tooltip("满足脱离条件之后，还要再坚持追这么多秒才真的放弃。留一点缓冲，玩家跑开几步不会立刻脱锁")]
    public float loseDelay = 1.0f;

    [Header("追击 · 速度")]
    [Tooltip("追击速度（像素/秒）。注意这是【绝对速度】，不是巡逻速度的倍率。\n" +
             "玩家跑步是 140 像素/秒：这个值必须接近它，否则玩家一跑就永远追不上、" +
             "而且一两秒内就会脱锁（这正是之前配置的毛病：40 × 1.7 = 68，只有玩家的一半）")]
    public float chaseSpeed = 115f;
    [Header("追击 · 平台边缘")]
    [Tooltip("追击时走到平台边缘怎么办：\n" +
             "  勾上 = 跟着玩家下去（下平台、跳台阶）—— 想要\"敌人会从台子上跳下来追你\"就用这个\n" +
             "  取消 = 停在边缘等玩家回来\n" +
             "注意：只有【下方 Chase Max Drop 像素之内有地面】时才会走下去；" +
             "下面是深渊就停在边上，不会自杀")]
    public bool chaseOffLedges = true;
    [Tooltip("允许往下跳的最大高度（像素）。超过这个深度就当成深渊，不会跳下去")]
    public float chaseMaxDrop = 80f;
    [Tooltip("探测下方深度时最长看多远（像素）")]
    public float fallCheckDistance = 240f;

    [Header("掉出关卡自动销毁")]
    [Tooltip("比出生点低这么多像素之后自动销毁，避免掉出世界的敌人一直在跑")]
    public float fallDestroyDepth = 480f;
    [Tooltip("追击时精灵染成这个颜色（巡逻时用原本的颜色）")]
    public Color chaseColor = new Color(1f, 0.72f, 0.2f, 1f);

    [Tooltip("被玩家弹刀弹开后的僵直时长（秒），给玩家留出反击窗口")]
    public float parriedStaggerTime = 0.5f;

    [Header("冲锋兵专用")]
    [Tooltip("离玩家多近就开始蓄力（像素）")]
    public float chargeTriggerRange = 110f;
    [Tooltip("前摇时长（秒）—— 这段时间它会变色定住，给玩家反应和弹刀的时间")]
    public float chargeWindup = 0.55f;
    [Tooltip("冲撞速度（像素/秒）。玩家跑步是 140，所以要明显更快才有威胁")]
    public float chargeSpeed = 280f;
    [Tooltip("冲撞最多持续多久（秒）")]
    public float chargeDuration = 0.75f;
    [Tooltip("撞完之后的硬直（秒）—— 玩家的反击窗口")]
    public float chargeRecover = 1.1f;
    [Tooltip("前摇期间的颜色（用来做预警）")]
    public Color windupColor = new Color(1f, 0.35f, 0.3f, 1f);

    [Header("重甲兵专用")]
    [Tooltip("勾上之后被打不会后退、也不会僵直（血厚但动作慢的代价）")]
    public bool ignoreKnockback = false;

    [Tooltip("被弹开时的水平速度（世界单位/秒）")]
    public float parriedKnockbackX = 7f;
    [Tooltip("被弹开时的上弹速度（世界单位/秒）")]
    public float parriedKnockbackY = 4f;

    [Header("接触伤害")]
    public int contactDamage = 1;
    [Tooltip("两次接触伤害之间的间隔（秒）。玩家的无敌帧才是主保险，这个只是兜底")]
    public float contactDamageInterval = 0.6f;

    [Header("受击反馈")]
    [Tooltip("被打退时的水平速度（世界单位/秒）")]
    public float knockbackX = 3f;
    [Tooltip("被打退时的上弹速度（世界单位/秒）")]
    public float knockbackY = 2f;
    [Tooltip("被打退后的僵直时长（秒），这期间不主动移动")]
    public float staggerTime = 0.15f;

    [Header("死亡")]
    public Color deathDustColor = new Color(1f, 0.55f, 0.55f, 1f);
    public int deathDustCount = 8;

    [Header("自检")]
    [Tooltip("勾上之后每秒都打一条状态日志（平时不用开）。" +
             "默认情况下只在检测到异常（疯狂掉头 / 想动却没动）时才打警告")]
    public bool verboseProbeLog = false;

    Rigidbody2D _rb;
    BoxCollider2D _box;
    Health _health;
    SpriteRenderer _sprite;
    Color _baseColor;
    Sprite _dustSprite;

    int _dir;
    float _contactTimer;
    float _staggerTimer;
    Mode _mode = Mode.Patrol;
    PlayerController _playerTarget;

    // 自检用
    float _lifeTime;
    float _probeTimer;
    Vector2 _probeRefPos;
    Vector2 _probeLastPos;
    float _probePathPx;    // 这一秒里走过的路程（掉头也算），判断"速度对不对"看它
    int _flipCount;
    float _loseTimer;      // 满足脱离条件之后已经持续了多久
    bool _ledgeStopLogged;
    float _spawnYPx;       // 出生点的高度（像素），用来判断有没有掉出关卡
    float _contactLogTimer; // "接触却没扣血"这条警告的节流计时

    // 冲锋兵的状态机
    enum ChargePhase { None, Windup, Dash, Recover }
    ChargePhase _chargePhase = ChargePhase.None;
    float _chargeTimer;
    int _chargeDir;

    // 难度系统用的基准值
    bool _baseRecorded;
    int _baseMaxHealth;
    float _baseMoveSpeed;
    float _baseChaseSpeed;
    float _baseChargeSpeed;
    int _baseContactDamage;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _health = GetComponent<Health>();
        _sprite = GetComponentInChildren<SpriteRenderer>();

        // 找玩家。TestLevelBootstrap 是先建玩家再建敌人的，所以这里能直接找到。
        // 找不到也不影响巡逻，只是不会追人。
        _playerTarget = FindFirstObjectByType<PlayerController>();
        if (_playerTarget == null)
        {
            Debug.LogWarning("[EnemyPatrol] 场景里没找到 PlayerController，这个敌人只会巡逻、不会追击。", this);
        }

        // 敌人身上有两个 BoxCollider2D（实心的 + 触发器）。
        // 不能用 GetComponent<BoxCollider2D>() —— 那依赖组件排列顺序，
        // 万一拿到的是触发器，算出来的"脚底"位置就是错的，探路射线会全部失效。
        // 这里明确挑出非 Trigger 的那个。
        _box = null;
        BoxCollider2D[] boxes = GetComponents<BoxCollider2D>();
        for (int i = 0; i < boxes.Length; i++)
        {
            if (!boxes[i].isTrigger)
            {
                _box = boxes[i];
                break;
            }
        }
        if (_box == null && boxes.Length > 0) _box = boxes[0];

        if (_sprite != null) _baseColor = _sprite.color;

        _dir = startDirection >= 0 ? 1 : -1;
        if (_sprite != null) _sprite.flipX = _dir < 0;

        _rb.freezeRotation = true;

        // 重力由脚本自己施加，不用 Unity 的全局重力。
        // 原因：本工程的安装清单里把 Project Settings → Physics 2D → Gravity 设成了 (0, 0)
        //（约定"重力由脚本接管"）。如果这里还用 gravityScale = 1 靠全局重力，
        // 乘出来是 0 —— 敌人会永远悬在出生点半空中，探路射线够不到地面，
        // 表现就是"每帧掉头、原地抽搐，被玩家踩一下才落到地上开始走"。
        _rb.gravityScale = 0f;

        // ---- 这两个是"Dynamic 刚体明明设了速度却纹丝不动"的两大元凶 ----
        // 1. 睡眠：刚体静止一会儿后会被物理引擎挂起，此时再赋 velocity 也不会动。
        //    我们既然是每帧手动驱动它，就必须禁止它睡觉。
        _rb.sleepMode = RigidbodySleepMode2D.NeverSleep;

        // 2. 摩擦力：脚底和地面的摩擦会跟我们的速度赋值对着干。
        //    给敌人的实心碰撞体一个零摩擦材质，让它说走就走。
        var frictionless = new PhysicsMaterial2D("EnemyFrictionless")
        {
            friction = 0f,
            bounciness = 0f
        };
        if (_box != null) _box.sharedMaterial = frictionless;

        _probeRefPos = transform.position;
        _probeLastPos = transform.position;
        _spawnYPx = transform.position.y * PlayerController.PixelsPerUnit;

        // 只建一次，死亡时反复新建贴图会漏内存
        _dustSprite = Greybox.Solid(Color.white, 2, 2);

        // 开局确认一次设置真的生效了（每个敌人一条，方便对照）
        Debug.Log(string.Format(
            "[EnemyPatrol:{0}] 初始化完成 · sleepMode={1} · 零摩擦材质={2} · 重力={3} · 碰撞体={4}",
            name, _rb.sleepMode, _box != null && _box.sharedMaterial != null,
            _rb.gravityScale, _box != null ? _box.size.ToString() : "无"), this);
    }

    void OnEnable()
    {
        if (_health != null)
        {
            _health.Damaged += OnDamaged;
            _health.Died += OnDied;
        }
    }

    void OnDisable()
    {
        if (_health != null)
        {
            _health.Damaged -= OnDamaged;
            _health.Died -= OnDied;
        }
    }

    void Update()
    {
        if (_contactTimer > 0f) _contactTimer = Mathf.Max(_contactTimer - Time.deltaTime, 0f);
        if (_contactLogTimer > 0f) _contactLogTimer = Mathf.Max(_contactLogTimer - Time.deltaTime, 0f);
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        DiagnoseMotion(dt);

        if (_health != null && _health.IsDead) return;

        // 掉出关卡就销毁自己。追击时会主动走下平台，万一走进深渊，
        // 不清理的话它会永远往下掉、一直占着 FixedUpdate 在跑。
        float yPx = transform.position.y * PlayerController.PixelsPerUnit;
        if (_spawnYPx - yPx > fallDestroyDepth)
        {
            // 掉下去的【不算玩家杀的】—— 成就的"不杀"和"杀光"都按这条区分。
            // 这一条和 OnDied 是两条不同的消失路径，所以两边都要登记。
            AchievementManager.NotifyEnemyFell(this);

            Debug.Log(string.Format(
                "[EnemyPatrol:{0}] 掉出关卡（y={1:0} 像素，比出生点低 {2:0}），自动销毁",
                name, yPx, _spawnYPx - yPx), this);
            Destroy(gameObject);
            return;
        }

        // 被打退期间不主动移动，否则击退会被下一帧的速度覆盖掉
        if (_staggerTimer > 0f)
        {
            _staggerTimer -= dt;
            return;
        }

        UpdateMode();

        // 冲锋兵在追击时走自己的状态机（蓄力 → 冲撞 → 硬直），其他敌人直接追
        if (_mode == Mode.Chase && kind == Kind.Charger) UpdateCharger(dt);
        else if (_mode == Mode.Chase) DoChase();
        else DoPatrol();

        // 兜底：万一还有别的东西把它弄睡了（比如别的脚本调了 Sleep()，或者
        // sleepMode 因为某种原因没生效），这里每帧主动唤醒一次。
        // 睡眠中的刚体是不会理 velocity 赋值的 —— 表现就是"站着不动，
        // 被玩家碰一下才突然开始走"。
        if (_rb.IsSleeping()) _rb.WakeUp();

        ApplyVerticalMotion(dt);
    }

    // ---------------------------------------------------------------- 巡逻 / 追击
    void DoPatrol()
    {
        if (!HasGroundAhead() || HasWallAhead()) Flip();
        SetHorizontalSpeed(moveSpeed);
    }

    void DoChase()
    {
        if (_playerTarget == null) { SetHorizontalSpeed(moveSpeed); return; }

        // 面朝玩家
        float dx = _playerTarget.transform.position.x - transform.position.x;
        FacePlayerWithDeadZone(dx);

        // 前方没地面 = 到了平台边缘。
        // 这一步是"敌人只追一小段就不追了"和"台子上的敌人不下来"的共同原因：
        // 以前无条件停在边缘（v.x = 0），敌人定住不动，玩家继续跑，
        // 距离很快超过 loseRange 就脱锁了 —— 速度配多大都没用。
        if (!HasGroundAhead())
        {
            if (!chaseOffLedges || !CanDropDownAhead())
            {
                SetHorizontalSpeed(0f);
                LogLedgeStopOnce();
                return;
            }
            _ledgeStopLogged = false;      // 能下去，清掉"被卡住"的一次性提示
        }

        if (HasWallAhead()) { SetHorizontalSpeed(0f); return; }

        SetHorizontalSpeed(chaseSpeed);
    }

    /// <summary>
    /// 脚下正下方有多深（像素）。返回 -1 表示在 fallCheckDistance 之内什么都没探到。
    /// 用来判断"从平台边缘走下去会不会摔进深渊"。
    /// </summary>
    float GroundDepthBelowPx()
    {
        if (_box == null) return -1f;

        const float ppu = PlayerController.PixelsPerUnit;

        Bounds b = _box.bounds;
        Vector2 origin = new Vector2(b.center.x, b.center.y);
        float maxDist = b.extents.y + fallCheckDistance / ppu;

        RaycastHit2D[] hits = Physics2D.RaycastAll(origin, Vector2.down, maxDist);
        float best = -1f;

        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D c = hits[i].collider;
            if (c == null || c.isTrigger) continue;
            if (c.transform == transform || c.transform.IsChildOf(transform)) continue;

            Health other = c.GetComponentInParent<Health>();
            if (other != null && other != _health) continue;

            // distance 是从身体中段算起的，减掉半高才是"脚底往下多少"
            float depthPx = (hits[i].distance - b.extents.y) * ppu;
            if (depthPx < 0f) depthPx = 0f;
            if (best < 0f || depthPx < best) best = depthPx;
        }

        return best;
    }

    /// <summary>前方边缘下面是不是"跳得下去"的高度（不是深渊）。</summary>
    bool CanDropDownAhead()
    {
        float depth = GroundDepthBelowPx();
        if (depth < 0f) return false;          // 探到底都没地面 → 深渊
        return depth <= chaseMaxDrop;
    }

    // ---------------------------------------------------------------- 冲锋兵
    /// <summary>
    /// 冲锋兵的追击状态机：
    ///   None    → 先朝玩家靠近，进入 chargeTriggerRange 就转 Windup
    ///   Windup  → 定住不动 + 变红（前摇，给玩家反应和弹刀的时间）
    ///   Dash    → 高速直线冲撞，撞墙或冲够时间就转 Recover
    ///   Recover → 硬直不动（玩家的反击窗口）
    ///
    /// 这个"有前摇的攻击"是整个游戏里第一个能被【弹刀】读出来的动作：
    /// 看到变红就按 E，撞上来那一下会被弹开 —— 弹刀终于有真正的用武之地了。
    /// </summary>
    void UpdateCharger(float dt)
    {
        const float ppu = PlayerController.PixelsPerUnit;

        switch (_chargePhase)
        {
            case ChargePhase.None:
                {
                    if (_playerTarget == null) { DoChase(); return; }

                    float dx = _playerTarget.transform.position.x - transform.position.x;
                    FacePlayerWithDeadZone(dx);

                    if (Mathf.Abs(dx) * ppu <= chargeTriggerRange) { EnterChargePhase(ChargePhase.Windup); return; }

                    // 靠近的过程中它也怕悬崖，会停住 —— 只有冲撞阶段才不管不顾
                    if (!HasGroundAhead() || HasWallAhead()) { SetHorizontalSpeed(0f); return; }
                    SetHorizontalSpeed(chaseSpeed);
                    break;
                }

            case ChargePhase.Windup:
                {
                    _chargeTimer -= dt;
                    SetHorizontalSpeed(0f);          // 前摇期间定住，玩家能看清预警
                    if (_chargeTimer <= 0f)
                    {
                        _chargeDir = _dir;
                        EnterChargePhase(ChargePhase.Dash);
                    }
                    break;
                }

            case ChargePhase.Dash:
                {
                    _chargeTimer -= dt;
                    _dir = _chargeDir;
                    SetHorizontalSpeed(chargeSpeed);

                    // 撞墙就停；冲够时间也停。冲撞期间【不】检查悬崖 —— 冲下去是它自己的事
                    if (_chargeTimer <= 0f || HasWallAhead())
                    {
                        EnterChargePhase(ChargePhase.Recover);
                    }
                    break;
                }

            case ChargePhase.Recover:
                {
                    _chargeTimer -= dt;
                    SetHorizontalSpeed(0f);
                    if (_chargeTimer <= 0f) EnterChargePhase(ChargePhase.None);
                    break;
                }
        }
    }

    void EnterChargePhase(ChargePhase next)
    {
        _chargePhase = next;

        switch (next)
        {
            case ChargePhase.Windup:
                _chargeTimer = chargeWindup;
                AudioManager.Play("sfx_charge");     // 前摇必须有声音，不然玩家只能靠眼睛看颜色
                Debug.Log(string.Format("[EnemyPatrol:{0}] 冲锋兵：开始蓄力（{1:0.00} 秒后冲撞，方向 {2}）",
                          name, chargeWindup, _dir), this);
                break;
            case ChargePhase.Dash:
                _chargeTimer = chargeDuration;
                break;
            case ChargePhase.Recover:
                _chargeTimer = chargeRecover;
                break;
            default:
                _chargeTimer = 0f;
                break;
        }

        ApplySpriteColor();     // 前摇要变色，必须立刻刷新
    }

    void LogLedgeStopOnce()
    {
        if (_ledgeStopLogged) return;
        _ledgeStopLogged = true;

        Debug.Log(string.Format(
            "[EnemyPatrol:{0}] 追击时在平台边缘停住了。原因：{1}。" +
            "（如果现象是\"追一小段就不追了\"，就是这里；" +
            "想让它跟着玩家跳下去，勾上 Chase Off Ledges，并确认 Chase Max Drop = {2:0} 像素够用）",
            name,
            chaseOffLedges ? "边缘下方超过 Chase Max Drop，判定为深渊" : "Chase Off Ledges 关着",
            chaseMaxDrop), this);
    }

    void UpdateMode()
    {
        if (_playerTarget == null) return;

        const float ppu = PlayerController.PixelsPerUnit;

        Vector2 toPlayer = (Vector2)_playerTarget.transform.position - (Vector2)transform.position;
        float distancePx = toPlayer.magnitude * ppu;
        float heightPx = Mathf.Abs(toPlayer.y) * ppu;

        if (_mode == Mode.Patrol)
        {
            if (distancePx <= detectRange && heightPx <= detectHeightLimit)
            {
                SetMode(Mode.Chase, string.Format(
                    "进入侦测：距离 {0:0}px ≤ {1:0}，高度差 {2:0}px ≤ {3:0}",
                    distancePx, detectRange, heightPx, detectHeightLimit));
            }
            return;
        }

        // ---- 已经在追击：判断要不要放弃 ----
        bool outOfRange = distancePx > loseRange || heightPx > loseHeightLimit;

        if (!outOfRange)
        {
            _loseTimer = 0f;
            return;
        }

        // 超出了一点点还不急着放弃，先追一会儿 —— 否则玩家在边界附近来回走，
        // 敌人会"追一下、停一下"，看起来像抽搐
        _loseTimer += Time.fixedDeltaTime;
        if (_loseTimer < loseDelay) return;

        SetMode(Mode.Patrol, string.Format(
            "脱离追击：距离 {0:0}px（上限 {1:0}）、高度差 {2:0}px（上限 {3:0}），已持续 {4:0.0} 秒",
            distancePx, loseRange, heightPx, loseHeightLimit, _loseTimer));
        _loseTimer = 0f;
    }

    void SetMode(Mode m, string reason)
    {
        if (_mode == m) return;

        Mode old = _mode;
        _mode = m;
        _loseTimer = 0f;
        _ledgeStopLogged = false;

        // 被脱离追击时，冲锋的状态机要一起清掉，不然会卡在"冲撞中"
        if (m == Mode.Patrol) _chargePhase = ChargePhase.None;

        ApplySpriteColor();

        // 打日志是故意的：追击"锁不住"这类问题，光看现象猜不出来，
        // 必须知道是哪一条条件触发的。这条日志会直接告诉你原因。
        Debug.Log(string.Format("[EnemyPatrol:{0}] 模式 {1} → {2}　原因：{3}",
                  name, old, m, reason), this);
    }

    /// <summary>把精灵恢复成"当前状态该有的颜色"。闪白之后也要调它，否则会把追击色冲掉。</summary>
    void ApplySpriteColor()
    {
        if (_sprite == null) return;

        // 冲锋兵蓄力时用预警色盖掉一切 —— 这个颜色就是给玩家看的"要来了"
        if (_chargePhase == ChargePhase.Windup) { _sprite.color = windupColor; return; }

        _sprite.color = _mode == Mode.Chase ? chaseColor : _baseColor;
    }

    /// <summary>
    /// 面朝玩家，但带一个死区。
    ///
    /// 玩家几乎站在敌人正上方时，dx 会在 0 附近来回抖（他的 knockback、跳跃、或者
    /// 你们两个刚撞过），敌人就会疯狂左右翻 —— 日志里出现过"掉头=22 次/秒"。
    /// 那种抖动在画面上非常明显，而且会让敌人的位移几乎归零。
    /// 几像素的偏差不值得翻一次身。
    /// </summary>
    void FacePlayerWithDeadZone(float dx)
    {
        const float flipDeadZonePx = 8f;

        if (Mathf.Abs(dx) * PlayerController.PixelsPerUnit < flipDeadZonePx) return;

        int wantDir = dx >= 0f ? 1 : -1;
        if (wantDir != _dir) Flip();
    }

    void SetHorizontalSpeed(float speedPx)
    {
        Vector2 v = _rb.linearVelocity;
        v.x = _dir * speedPx / PlayerController.PixelsPerUnit;
        _rb.linearVelocity = v;
    }

    /// <summary>自己施加垂直运动（全局重力被设成 0 了，不能指望 Unity）</summary>
    void ApplyVerticalMotion(float dt)
    {
        Vector2 v = _rb.linearVelocity;

        if (IsOnGround())
        {
            if (v.y < 0f) v.y = 0f;
        }
        else
        {
            v.y = Mathf.Max(v.y - gravity / PlayerController.PixelsPerUnit * dt,
                            -maxFallSpeed / PlayerController.PixelsPerUnit);
        }

        _rb.linearVelocity = v;
    }

    /// <summary>
    /// 身体正下方有没有东西（用来判断该不该继续施加重力）。
    /// 从身体中段往下打，长度覆盖到脚底再往下一点点。
    /// </summary>
    bool IsOnGround()
    {
        if (_box == null) return false;

        Bounds b = _box.bounds;
        Vector2 origin = new Vector2(b.center.x, b.center.y);
        float distance = b.extents.y + 0.1f;      // 脚底再往下约 1.6 像素

        return HitsSomething(origin, Vector2.down, distance);
    }

    // ---------------------------------------------------------------- 探路
    bool HasGroundAhead()
    {
        if (_box == null) return true;

        Bounds b = _box.bounds;
        float x = _dir > 0 ? b.max.x + 0.02f : b.min.x - 0.02f;

        // 关键：从身体【中段】往下打，不要从脚底往下打。
        // 从脚底出发时，起点会贴着地面碰撞体的表面、甚至落进它内部，
        // 射线就永远"打不到地面"，敌人会误判成站在悬崖边，每帧掉头、原地抽搐。
        // 从身体中段出发，起点一定在地面之上，稳定得多。
        Vector2 origin = new Vector2(x, b.center.y);
        float distance = b.extents.y + edgeCheckDistance / PlayerController.PixelsPerUnit;

        return HitsSomething(origin, Vector2.down, distance);
    }

    bool HasWallAhead()
    {
        if (_box == null) return false;

        Bounds b = _box.bounds;
        Vector2 origin = new Vector2(b.center.x, b.center.y);
        float distance = b.extents.x + wallCheckDistance / PlayerController.PixelsPerUnit;
        return HitsSomething(origin, new Vector2(_dir, 0f), distance);
    }

    /// <summary>
    /// 射线检测。会跳过：自己身上的碰撞体、所有 Trigger、以及其他"角色"
    /// （玩家和别的敌人）。
    ///
    /// 最后那条很关键：如果不跳过，敌人一靠近玩家就会认为"前面是墙"而掉头，
    /// 两个敌人叠在一起时更会互相判定成墙，原地疯狂抽搐、一步都走不动。
    /// </summary>
    bool HitsSomething(Vector2 origin, Vector2 direction, float distance)
    {
        RaycastHit2D[] hits = Physics2D.RaycastAll(origin, direction, distance);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D c = hits[i].collider;
            if (c == null || c.isTrigger) continue;
            if (c.transform == transform || c.transform.IsChildOf(transform)) continue;

            // 别的角色（有 Health 的）不算地形
            Health other = c.GetComponentInParent<Health>();
            if (other != null && other != _health) continue;

            return true;
        }
        return false;
    }

    void Flip()
    {
        _dir = -_dir;
        _flipCount++;
        if (_sprite != null) _sprite.flipX = _dir < 0;
    }

    // ---------------------------------------------------------------- 自检
    /// <summary>
    /// 每秒检查一次：如果"疯狂掉头"或者"想动却没动"，就打一条警告，把关键数据一起带出来。
    /// 出生后前 3 秒无条件打两条基线日志，保证即使一切静止也能拿到数据。
    /// </summary>
    void DiagnoseMotion(float dt)
    {
        _lifeTime += dt;
        _probeTimer += dt;

        // 【路程】和【净位移】都要算，别看错指标：
        //   净位移 = 起点到终点的直线距离。敌人掉头往回走时它接近 0，看起来像"没动"，
        //            但其实走了一个来回 —— 我因为这个误判过一次，以为敌人还卡着。
        //   路程   = 这一秒里真正走过的距离，掉头也不影响。判断"速度对不对"要看它。
        Vector2 pos = transform.position;
        if (_probeLastPos != Vector2.zero)
        {
            _probePathPx += Vector2.Distance(pos, _probeLastPos) * PlayerController.PixelsPerUnit;
        }
        _probeLastPos = pos;

        if (_probeTimer < 1f) return;

        float seconds = _probeTimer;
        _probeTimer = 0f;

        float movedPx = Vector2.Distance(transform.position, _probeRefPos) * PlayerController.PixelsPerUnit;
        float pathPx = _probePathPx;
        _probePathPx = 0f;
        _probeRefPos = transform.position;

        // 期望位移必须用【当前模式真正在用的速度】。
        // 这里以前一律用 moveSpeed（巡逻速度 40），而追击速度是 chaseSpeed（115）——
        // 结果追击时"该动却没动"永远判不出来，诊断对"追着追着停住了"这类故障完全是瞎的。
        float currentSpeed = _mode == Mode.Chase ? chaseSpeed : moveSpeed;
        float expectedPx = currentSpeed * seconds;

        float distancePx = -1f;
        float heightPx = -1f;
        if (_playerTarget != null)
        {
            Vector2 toPlayer = (Vector2)_playerTarget.transform.position - (Vector2)transform.position;
            distancePx = toPlayer.magnitude * PlayerController.PixelsPerUnit;
            heightPx = Mathf.Abs(toPlayer.y) * PlayerController.PixelsPerUnit;
        }

        // 判"卡住"要看【路程】，不是净位移 ——
        // 净位移在掉头时必然接近 0，会把正常巡逻误报成卡死。
        bool stalled = expectedPx > 5f && pathPx < expectedPx * 0.3f;
        bool jittering = _flipCount > 10;
        bool early = _lifeTime <= 1.6f;

        if (verboseProbeLog || early || stalled || jittering)
        {
            string msg = string.Format(
                "[EnemyPatrol:{0}] t={1:0.0}s 模式={2} dir={3} 路程={4:0.0}px 净位移={5:0.0}px（期望≈{6:0.0}px）" +
                " 掉头={7} 距玩家={8:0}px 高度差={9:0}px" +
                " || groundAhead={10} wallAhead={11} 可下平台={12} 脚下有地={13}" +
                " || vx={14:0.00} vy={15:0.00} 僵直={16:0.00} 脱锁计时={17:0.00}" +
                " 脚底y={18:0.00} 睡眠中={19}",
                name, _lifeTime, _mode, _dir, pathPx, movedPx, expectedPx,
                _flipCount, distancePx, heightPx,
                HasGroundAhead(), HasWallAhead(), CanDropDownAhead(), IsOnGround(),
                _rb != null ? _rb.linearVelocity.x : 0f,
                _rb != null ? _rb.linearVelocity.y : 0f,
                _staggerTimer, _loseTimer,
                _box != null ? _box.bounds.min.y : 0f,
                _rb != null && _rb.IsSleeping());

            if (stalled || jittering)
            {
                Debug.LogWarning(msg + "\n（这条是异常警告，请把整行发给我）", this);
            }
            else
            {
                Debug.Log(msg, this);
            }
        }

        _flipCount = 0;
        _probeRefPos = transform.position;
    }

    // ---------------------------------------------------------------- 接触伤害 / 弹刀
    void OnTriggerStay2D(Collider2D other)
    {
        if (_health != null && _health.IsDead) return;

        var player = other.GetComponentInParent<PlayerController>();
        if (player == null) return;

        // 玩家在弹刀窗口内 → 这一下不算数，反而把敌人弹开
        PlayerCombat combat = player.GetComponent<PlayerCombat>();
        if (combat != null && combat.TryParry())
        {
            GetParried(player.transform.position);
            _contactTimer = contactDamageInterval;
            return;
        }

        if (_contactTimer > 0f) return;

        // 真正的频率控制靠玩家自己的无敌帧，这里只是兜底
        if (player.TakeDamage(contactDamage, transform.position))
        {
            _contactTimer = contactDamageInterval;
            return;
        }

        // ---- 接触了但没扣到血：把原因打出来 ----
        // "碰到敌人却不掉血"这种问题，光看画面是查不出来的：
        // 可能是玩家在无敌帧、可能是弹刀窗口还开着、也可能压根没走到这里。
        // 所以这里把每一条可能的原因都直接打出来（每秒最多一条，不刷屏）。
        if (_contactLogTimer > 0f) return;
        _contactLogTimer = 1f;

        Health playerHealth = player.GetComponent<Health>();
        Debug.LogWarning(string.Format(
            "[EnemyPatrol:{0}] 和玩家接触了，但没扣到血。\n" +
            "  玩家无敌中 = {1}（受伤无敌剩余 {2:0.00} 秒，冲刺/弹刀无敌剩余另算）\n" +
            "  玩家是否处在弹刀窗口 = {3}\n" +
            "  玩家血量 = {4}\n" +
            "  我的接触冷却 = {5:0.00} 秒\n" +
            "  玩家是否已死 = {6}",
            name,
            playerHealth != null && playerHealth.IsInvincible,
            playerHealth != null ? playerHealth.InvincibleTimer : -1f,
            combat != null && combat.IsParryWindowActive,
            playerHealth != null ? playerHealth.currentHealth : -1,
            _contactTimer,
            playerHealth != null && playerHealth.IsDead), this);
    }

    // ---------------------------------------------------------------- 受击
    void OnDamaged(int amount, Vector2 sourcePosition)
    {
        // 重甲兵：打不退、也不僵直。血厚动作慢的代价换来"打不动"的压迫感。
        // 注意这里仍然会闪白 —— 玩家必须能确认"我打中了"，只是推不动它。
        if (ignoreKnockback)
        {
            StartCoroutine(FlashRoutine());
            return;
        }

        float dirX = transform.position.x >= sourcePosition.x ? 1f : -1f;
        _rb.linearVelocity = new Vector2(dirX * knockbackX, knockbackY);
        _staggerTimer = staggerTime;

        StartCoroutine(FlashRoutine());
    }

    IEnumerator FlashRoutine()
    {
        if (_sprite == null) yield break;

        _sprite.color = Color.white;
        yield return new WaitForSeconds(0.08f);

        // 用 ApplySpriteColor 而不是直接写 _baseColor ——
        // 否则闪白结束后会把"追击中"的染色冲掉，敌人颜色和状态对不上。
        ApplySpriteColor();
    }

    /// <summary>
    /// 被玩家弹刀弹开。方向是从玩家指向自己（被推开），并给一段较长的僵直，
    /// 让玩家弹完能立刻接一刀。
    /// </summary>
    void GetParried(Vector2 fromPosition)
    {
        // ---- 弹刀奖励按"这一下有多难挡"来给 ----
        // 普通接触伤害（走过来撞你）→ 基础僵直
        // 冲锋兵的前摇 / 冲撞        → 2.4 倍僵直
        // 理由：冲锋兵有明确的可视化预警，读招并成功弹开是【技术展示】，
        // 奖励必须明显更大，否则玩家没有动机去练弹刀，只会一直后撤。
        bool telegraphed = _chargePhase == ChargePhase.Windup || _chargePhase == ChargePhase.Dash;
        float stagger = telegraphed ? parriedStaggerTime * 2.4f : parriedStaggerTime;

        float dirX = transform.position.x >= fromPosition.x ? 1f : -1f;
        _rb.linearVelocity = new Vector2(dirX * parriedKnockbackX, parriedKnockbackY);
        _staggerTimer = stagger;

        // 冲锋状态必须一起清掉，否则硬直结束后会接着冲 —— 弹刀就白弹了
        _chargePhase = ChargePhase.None;
        _chargeTimer = 0f;

        // 故意【不】退出追击模式。
        // 以前这里会 SetMode(Patrol)，于是"被弹一下"等于"敌人放弃追你"，
        // 玩家只要无脑连按弹刀就能让敌人永远不追 —— 而且一旦弹刀判定出问题，
        // 表现会是"敌人碰到我就停下来"，非常难查。
        // 弹刀该给的奖励是僵直和击退，不是让敌人失忆。
        StartCoroutine(FlashRoutine());

        Debug.Log(string.Format(
            "[EnemyPatrol:{0}] 被弹刀弹开：僵直 {1:0.00} 秒{2}，当前模式 {3}",
            name, stagger, telegraphed ? "（挡住了冲锋，奖励加倍）" : "", _mode), this);
    }

    // ---------------------------------------------------------------- 难度
    /// <summary>
    /// 记住"基准数值"。难度系统要在玩家选完档位之后重新算一遍，
    /// 所以必须先把出厂值存下来 —— 否则第二次切换难度就是在上一档的结果上再乘一次，
    /// 切几次之后就完全失控了。
    /// </summary>
    public void RecordBaseStats()
    {
        _baseRecorded = true;
        _baseMaxHealth = _health != null ? _health.maxHealth : 2;
        _baseMoveSpeed = moveSpeed;
        _baseChaseSpeed = chaseSpeed;
        _baseChargeSpeed = chargeSpeed;
        _baseContactDamage = contactDamage;
    }

    /// <summary>按难度重新配置。基准值没记过就先记一次。</summary>
    public void ApplyDifficulty(float healthMul, float speedMul, int newContactDamage)
    {
        if (!_baseRecorded) RecordBaseStats();

        if (_health != null)
        {
            int hp = Mathf.Max(1, Mathf.RoundToInt(_baseMaxHealth * healthMul));
            _health.maxHealth = hp;
            _health.currentHealth = hp;
        }

        moveSpeed = _baseMoveSpeed * speedMul;
        chaseSpeed = _baseChaseSpeed * speedMul;
        contactDamage = newContactDamage;

        // 冲锋兵的冲撞速度也应该跟着难度走，不然它还是会用同一套节奏
        chargeSpeed = _baseChargeSpeed * speedMul;
    }

    void OnDied()
    {
        // 血扣光 = 被玩家杀的（这个游戏里没有别的来源能伤到敌人）
        AchievementManager.NotifyEnemyKilled(this);

        AudioManager.Play("sfx_enemy_death");
        SpawnDeathDust();
        Destroy(gameObject);
    }

    void SpawnDeathDust()
    {
        const float ppu = PlayerController.PixelsPerUnit;

        for (int i = 0; i < deathDustCount; i++)
        {
            var go = new GameObject("EnemyDust");
            if (transform.parent != null) go.transform.SetParent(transform.parent, false);
            go.transform.position = transform.position;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _dustSprite;
            sr.color = deathDustColor;
            sr.sortingOrder = 12;

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float speed = Random.Range(40f, 110f) / ppu;

            var puff = go.AddComponent<DustPuff>();
            puff.velocity = new Vector2(Mathf.Cos(angle), Mathf.Abs(Mathf.Sin(angle))) * speed;
            puff.gravity = -16f;
            puff.lifetime = 0.45f;
            puff.fadeStart = 0.5f;
        }
    }
}
