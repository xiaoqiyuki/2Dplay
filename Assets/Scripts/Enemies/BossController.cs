using System.Collections;
using UnityEngine;

/// <summary>
/// Boss：多阶段 + 跳砸 + 冲击波 + 二阶段召唤。
///
/// 行为（刻意做得很"可读"，因为它只有两个动作）：
///   地面：朝玩家走过去；靠太近就停下（避免贴脸推着玩家跑）
///   跳砸：每隔 slamInterval 跳起来，在空中朝玩家飘，落地时向两侧各放一道冲击波
///   二阶段（血量低于 phase2Threshold）：走得更快、砸得更勤，每次砸完还召唤小兵
///
/// 血条直接画在 OnGUI 上，不依赖任何 UI 包。
///
/// 【为什么动作这么少】Boss 的难点应该来自"读招 + 走位"，不是来自动作数量。
/// 两个动作 + 二阶段变速，已经足够撑起一场 30 秒的战斗，而且每一招玩家都能看懂。
/// 以后要加招，往 DoSlam() 和 FixedUpdate 的分支里加就行。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Health))]
public class BossController : MonoBehaviour
{
    [Header("激活")]
    [Tooltip("玩家进入这个距离（像素）Boss 才会开始行动。默认 700 ≈ 一屏多一点。\n" +
             "没有这个的话，Boss 从关卡一开始就在跑 AI —— 它出生时往下掉一次，" +
             "落地会被当成一次「跳砸」，于是【关卡开头就会震屏 + 放出两道冲击波】，\n" +
             "哪怕玩家离它一万多像素远")]
    public float activationRange = 700f;

    public bool IsActivated { get; private set; }

    [Header("阶段")]
    [Tooltip("血量低于这个比例进入二阶段")]
    public float phase2Threshold = 0.5f;
    public float phase2SpeedMultiplier = 1.5f;
    public float phase2IntervalMultiplier = 0.6f;

    [Header("移动（像素/秒）")]
    public float walkSpeed = 46f;
    [Tooltip("离玩家近于这个距离就停下，避免把玩家顶在墙上摩擦")]
    public float stopRange = 36f;

    [Header("重力（自己施加）")]
    public float gravity = 1500f;
    public float maxFallSpeed = 640f;

    [Header("跳砸")]
    [Tooltip("每隔多久跳砸一次（秒）")]
    public float slamInterval = 3.2f;
    [Tooltip("起跳初速度（像素/秒）")]
    public float jumpVelocity = 430f;
    [Tooltip("腾空时朝玩家飘的速度（像素/秒）")]
    public float slamTravelSpeed = 95f;
    [Tooltip("砸地时的屏幕震动（像素）")]
    public float slamShakeMagnitude = 5f;

    [Header("冲击波")]
    public int shockwaveDamage = 1;
    public float shockwaveSpeed = 150f;
    public float shockwaveWidth = 12f;
    public float shockwaveHeight = 18f;
    public Color shockwaveColor = new Color(1f, 0.62f, 0.35f, 1f);

    [Header("接触伤害")]
    public int contactDamage = 2;
    public float contactDamageInterval = 0.8f;

    [Header("二阶段召唤")]
    public int summonCount = 1;
    public float summonCooldown = 7f;

    [Header("竞技场边界（像素，世界坐标）。0 = 不限制")]
    public float arenaLeftX = 0f;
    public float arenaRightX = 0f;

    [Header("表现")]
    public Color phase2Color = new Color(1f, 0.5f, 0.45f, 1f);
    public Color deathDustColor = new Color(1f, 0.7f, 0.5f, 1f);

    public bool IsPhase2 { get; private set; }

    Rigidbody2D _rb;
    BoxCollider2D _box;
    Health _health;
    SpriteRenderer _sprite;
    Sprite _shockwaveSprite;
    Sprite _dustSprite;

    float _slamTimer;
    float _contactTimer;
    float _summonTimer;
    bool _wasGrounded = true;
    bool _hasLandedOnce;
    bool _defeated;

    // 难度系统用的基准值
    bool _baseRecorded;
    int _baseMaxHealth;
    float _baseSlamInterval;
    int _baseContactDamage;
    int _baseShockwaveDamage;

    Vector3 _spawnPosition;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _health = GetComponent<Health>();
        _sprite = GetComponentInChildren<SpriteRenderer>();

        // 实心碰撞体（挡玩家、挡地形）+ 触发器（接触伤害）
        BoxCollider2D[] boxes = GetComponents<BoxCollider2D>();
        _box = null;
        for (int i = 0; i < boxes.Length; i++)
        {
            if (!boxes[i].isTrigger) { _box = boxes[i]; break; }
        }
        if (_box == null && boxes.Length > 0) _box = boxes[0];

        _rb.bodyType = RigidbodyType2D.Dynamic;
        _rb.gravityScale = 0f;          // 全局重力是 0，Boss 自己施加重力
        _rb.freezeRotation = true;
        _rb.sleepMode = RigidbodySleepMode2D.NeverSleep;

        if (_box != null)
        {
            _box.sharedMaterial = new PhysicsMaterial2D("BossFrictionless")
            {
                friction = 0f,
                bounciness = 0f
            };
        }

        _shockwaveSprite = Greybox.Solid(Color.white, (int)shockwaveWidth, (int)shockwaveHeight);
        _dustSprite = Greybox.Solid(Color.white, 3, 3);

        _slamTimer = slamInterval;
        _summonTimer = summonCooldown;
        _spawnPosition = transform.position;
    }

    void FixedUpdate()
    {
        if (_defeated) return;

        float dt = Time.fixedDeltaTime;
        Vector2 v = _rb.linearVelocity;

        // ---- 阶段 ----
        if (!IsPhase2 && _health != null && _health.maxHealth > 0 &&
            _health.currentHealth <= Mathf.CeilToInt(_health.maxHealth * phase2Threshold))
        {
            EnterPhase2();
        }

        // ---- 重力 ----
        bool grounded = IsOnGround();
        if (grounded)
        {
            if (v.y < 0f) v.y = 0f;
        }
        else
        {
            v.y = Mathf.Max(v.y - gravity / PlayerController.PixelsPerUnit * dt,
                            -maxFallSpeed / PlayerController.PixelsPerUnit);
        }

        // ---- 激活：玩家靠近之前，Boss 只在原地待着 ----
        if (!IsActivated)
        {
            if (PlayerDistanceX() <= activationRange) Activate();
        }

        float speed = walkSpeed * (IsPhase2 ? phase2SpeedMultiplier : 1f);
        float interval = slamInterval * (IsPhase2 ? phase2IntervalMultiplier : 1f);

        // ---- 落地检测：上一帧在空中、这一帧落地 → 砸地 ----
        // 两个前提都必须满足：
        //   1. 已经激活（否则关卡开头它出生时掉一次就会震屏）
        //   2. 不是第一次落地（出生下落的那一次不算"跳砸"）
        if (grounded && !_wasGrounded)
        {
            if (IsActivated && _hasLandedOnce) DoSlam();
            _hasLandedOnce = true;
        }
        _wasGrounded = grounded;

        if (!IsActivated)
        {
            // 没激活：只施加重力，不走路、不跳、不召唤
            _rb.linearVelocity = v;
            return;
        }

        _slamTimer -= dt;

        float dx = PlayerDx();

        if (grounded)
        {
            if (_slamTimer <= 0f)
            {
                _slamTimer = interval;
                v.y = jumpVelocity / PlayerController.PixelsPerUnit;
                Debug.Log(string.Format("[Boss] 跳砸（{0}阶段，下次间隔 {1:0.0} 秒）",
                          IsPhase2 ? "二" : "一", interval), this);
            }
            else
            {
                v.x = Mathf.Abs(dx) <= stopRange / PlayerController.PixelsPerUnit
                    ? 0f
                    : Mathf.Sign(dx) * speed / PlayerController.PixelsPerUnit;
            }
        }
        else
        {
            // 腾空时朝玩家飘，这样砸下去的位置会追着玩家
            v.x = Mathf.Sign(dx) * slamTravelSpeed / PlayerController.PixelsPerUnit;
        }

        _rb.linearVelocity = v;

        ClampToArena();

        // ---- 二阶段召唤 ----
        if (IsPhase2)
        {
            _summonTimer -= dt;
            if (_summonTimer <= 0f)
            {
                _summonTimer = summonCooldown;
                SummonMinions();
            }
        }
    }

    void Update()
    {
        if (_contactTimer > 0f) _contactTimer = Mathf.Max(_contactTimer - Time.deltaTime, 0f);
    }

    // ---------------------------------------------------------------- 招式
    void DoSlam()
    {
        // 两侧各来一道冲击波
        SpawnShockwave(1);
        SpawnShockwave(-1);

        AudioManager.Play("sfx_slam", 1f, 0.03f);

        Camera cam = Camera.main;
        if (cam != null)
        {
            PlatformerCamera follow = cam.GetComponent<PlatformerCamera>();
            if (follow != null) follow.Shake(0.22f, slamShakeMagnitude);
        }

        // 落地扬尘
        for (int i = 0; i < 10; i++)
        {
            var go = new GameObject("BossDust");
            if (transform.parent != null) go.transform.SetParent(transform.parent, false);
            go.transform.position = transform.position + new Vector3(Random.Range(-0.4f, 0.4f), -0.3f, 0f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _dustSprite;
            sr.color = shockwaveColor;
            sr.sortingOrder = 12;

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float spd = Random.Range(50f, 130f) / PlayerController.PixelsPerUnit;

            var puff = go.AddComponent<DustPuff>();
            puff.velocity = new Vector2(Mathf.Cos(angle), Mathf.Abs(Mathf.Sin(angle))) * spd;
            puff.gravity = -14f;
            puff.lifetime = 0.4f;
            puff.fadeStart = 0.5f;
        }
    }

    void SpawnShockwave(int dir)
    {
        var go = new GameObject("Shockwave");
        if (transform.parent != null) go.transform.SetParent(transform.parent, false);
        go.transform.position = (Vector2)transform.position
                                + new Vector2(dir * 0.7f, -(_box != null ? _box.bounds.extents.y : 0.4f) + 0.3f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _shockwaveSprite;
        sr.color = shockwaveColor;
        sr.sortingOrder = 11;

        var rb = go.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Kinematic;
        rb.gravityScale = 0f;

        var col = go.AddComponent<BoxCollider2D>();
        col.size = new Vector2(shockwaveWidth / PlayerController.PixelsPerUnit,
                               shockwaveHeight / PlayerController.PixelsPerUnit);
        col.isTrigger = true;

        var wave = go.AddComponent<Shockwave>();
        wave.direction = dir;
        wave.speed = shockwaveSpeed;
        wave.damage = shockwaveDamage;
    }

    void SummonMinions()
    {
        for (int i = 0; i < summonCount; i++)
        {
            float x = transform.position.x + Random.Range(-2.5f, 2.5f);
            var posPx = new Vector2(x * PlayerController.PixelsPerUnit,
                                    (transform.position.y + 1.5f) * PlayerController.PixelsPerUnit);

            GameObject minionGo = TestLevelBootstrap.SpawnEnemy(
                "BossMinion", posPx, EnemyPatrol.Kind.Grunt,
                transform.parent, new Color(1f, 0.62f, 0.72f, 1f),
                default, default, true);      // 最后一个参数 = 召唤出来的

            // 小兵是在 DifficultySettings.Apply() 之后才生成的，必须单独补一次难度，
            // 否则它用的是出厂数值 —— 简单难度下小兵会和一般难度一样强。
            if (minionGo != null) DifficultySettings.ApplyTo(minionGo.GetComponent<EnemyPatrol>());
        }

        Debug.Log(string.Format("[Boss] 二阶段：召唤了 {0} 个小兵", summonCount), this);
    }

    void EnterPhase2()
    {
        IsPhase2 = true;
        if (_sprite != null) _sprite.color = phase2Color;
        if (_sprite != null) transform.localScale = new Vector3(1.15f, 1.15f, 1f);
        _summonTimer = 1.2f;    // 进二阶段先来一波召唤

        Debug.Log("[Boss] 进入二阶段：移动更快、跳砸更频繁、开始召唤小兵", this);
    }

    // ---------------------------------------------------------------- 工具
    float PlayerDx()
    {
        var player = FindPlayer();
        if (player == null) return 0f;
        return player.transform.position.x - transform.position.x;
    }

    float PlayerDistanceX()
    {
        return Mathf.Abs(PlayerDx()) * PlayerController.PixelsPerUnit;
    }

    void Activate()
    {
        IsActivated = true;
        _slamTimer = slamInterval * 0.6f;    // 给玩家一点喘息时间再出第一招
        Debug.Log(string.Format("[Boss] 玩家进入 {0:0} 像素范围，Boss 开始行动", activationRange), this);
    }

    PlayerController _playerCache;

    PlayerController FindPlayer()
    {
        if (_playerCache == null) _playerCache = FindFirstObjectByType<PlayerController>();
        return _playerCache;
    }

    bool IsOnGround()
    {
        if (_box == null) return false;

        Bounds b = _box.bounds;
        Vector2 origin = new Vector2(b.center.x, b.center.y);
        float distance = b.extents.y + 0.12f;

        RaycastHit2D[] hits = Physics2D.RaycastAll(origin, Vector2.down, distance);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider2D c = hits[i].collider;
            if (c == null || c.isTrigger) continue;
            if (c.transform == transform || c.transform.IsChildOf(transform)) continue;
            if (c.GetComponentInParent<Health>() != null) continue;   // 角色不算地形
            return true;
        }
        return false;
    }

    /// <summary>别让 Boss 走出竞技场，否则它会一路走出关卡</summary>
    void ClampToArena()
    {
        if (arenaRightX - arenaLeftX < 1f) return;

        const float ppu = PlayerController.PixelsPerUnit;
        Vector2 p = _rb.position;
        float px = p.x * ppu;

        if (px < arenaLeftX)
        {
            px = arenaLeftX;
            p.x = px / ppu;
            _rb.position = p;
            _rb.linearVelocity = new Vector2(0f, _rb.linearVelocity.y);
        }
        else if (px > arenaRightX)
        {
            px = arenaRightX;
            p.x = px / ppu;
            _rb.position = p;
            _rb.linearVelocity = new Vector2(0f, _rb.linearVelocity.y);
        }
    }

    // ---------------------------------------------------------------- 接触伤害
    void OnTriggerStay2D(Collider2D other)
    {
        if (other == null || _defeated) return;
        if (_contactTimer > 0f) return;

        var player = other.GetComponentInParent<PlayerController>();
        if (player == null) return;

        // Boss 的接触伤害也能被弹刀挡下来 —— 高风险高回报
        PlayerCombat combat = player.GetComponent<PlayerCombat>();
        if (combat != null && combat.TryParry())
        {
            _contactTimer = contactDamageInterval;
            Debug.Log("[Boss] 攻击被玩家弹开了", this);
            return;
        }

        if (player.TakeDamage(contactDamage, transform.position))
        {
            _contactTimer = contactDamageInterval;
        }
    }

    // ---------------------------------------------------------------- 死亡
    void OnEnable()
    {
        if (_health != null)
        {
            _health.Died += OnDied;
            _health.Damaged += OnDamaged;
        }
    }

    void OnDisable()
    {
        if (_health != null)
        {
            _health.Died -= OnDied;
            _health.Damaged -= OnDamaged;
        }
    }

    void OnDamaged(int amount, Vector2 sourcePosition)
    {
        StartCoroutine(FlashRoutine());
    }

    IEnumerator FlashRoutine()
    {
        if (_sprite == null) yield break;

        Color baseColor = IsPhase2 ? phase2Color : Color.white;
        _sprite.color = Color.white;
        yield return new WaitForSeconds(0.07f);
        if (_sprite != null) _sprite.color = baseColor;
    }

    // ---------------------------------------------------------------- 难度
    /// <summary>记住基准数值。难度系统要用它重算，不能在上一档结果上再乘一次。</summary>
    public void RecordBaseStats()
    {
        _baseRecorded = true;
        // Health 是必挂组件（[RequireComponent]），正常一定拿得到；
        // 拿不到时用 20（一般难度的基准），不要引用别的类上的字段名。
        _baseMaxHealth = _health != null ? _health.maxHealth : 20;
        _baseSlamInterval = slamInterval;
        _baseContactDamage = contactDamage;
        _baseShockwaveDamage = shockwaveDamage;
    }

    public void ApplyDifficulty(int newHealth, float slamIntervalMul,
                                int newContactDamage, int newShockwaveDamage)
    {
        if (!_baseRecorded) RecordBaseStats();

        if (_health != null)
        {
            _health.maxHealth = Mathf.Max(1, newHealth);
            _health.currentHealth = _health.maxHealth;
        }

        slamInterval = _baseSlamInterval * slamIntervalMul;
        contactDamage = newContactDamage;
        shockwaveDamage = newShockwaveDamage;
        _slamTimer = slamInterval;
    }

    /// <summary>
    /// 把 Boss 送回出生点并清空状态。
    /// 玩家死亡复活时必须调 —— 否则 Boss 会一直站在复活点旁边，
    /// 玩家一睁眼就被再撞一次，变成"连死"。
    /// </summary>
    public void ResetToSpawn()
    {
        if (_defeated) return;
        if (_spawnPosition == Vector3.zero) return;

        _rb.linearVelocity = Vector2.zero;
        _rb.position = new Vector2(_spawnPosition.x, _spawnPosition.y);
        transform.position = _spawnPosition;

        _slamTimer = slamInterval;
        _wasGrounded = true;
        _hasLandedOnce = true;
        IsActivated = false;      // 玩家得重新走近才会再激活
    }

    void OnDied()
    {
        if (_defeated) return;
        _defeated = true;

        _rb.linearVelocity = Vector2.zero;
        _rb.bodyType = RigidbodyType2D.Static;

        Debug.Log("[Boss] ===== BOSS 已被击败 =====", this);

        AudioManager.PlayFlat("sfx_enemy_death", 1f);

        // 通知流程总控去播通关结算
        if (GameFlow.Instance != null) GameFlow.Instance.NotifyBossDefeated();

        StartCoroutine(DeathRoutine());
    }

    IEnumerator DeathRoutine()
    {
        // 炸成一堆方块
        for (int i = 0; i < 26; i++)
        {
            var go = new GameObject("BossDust");
            if (transform.parent != null) go.transform.SetParent(transform.parent, false);
            go.transform.position = transform.position + new Vector3(Random.Range(-0.6f, 0.6f), Random.Range(-0.6f, 0.6f), 0f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _dustSprite;
            sr.color = deathDustColor;
            sr.sortingOrder = 12;

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float spd = Random.Range(60f, 200f) / PlayerController.PixelsPerUnit;

            var puff = go.AddComponent<DustPuff>();
            puff.velocity = new Vector2(Mathf.Cos(angle), Mathf.Abs(Mathf.Sin(angle))) * spd;
            puff.gravity = -16f;
            puff.lifetime = 0.9f;
            puff.fadeStart = 0.6f;

            if (i % 4 == 0) yield return new WaitForSeconds(0.03f);
        }

        yield return new WaitForSeconds(0.5f);
        Destroy(gameObject);
    }

    // ---------------------------------------------------------------- 血条
    void OnGUI()
    {
        // 没激活就不画血条 —— 否则玩家在关卡开头就看到一条满血 Boss 血条
        if (_defeated || !IsActivated || _health == null || _health.maxHealth <= 0) return;

        float w = Mathf.Min(Screen.width * 0.6f, 520f);
        float h = 22f;
        float x = (Screen.width - w) * 0.5f;
        float y = 24f;

        float ratio = Mathf.Clamp01((float)_health.currentHealth / _health.maxHealth);

        // 底板
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, w + 6f, h + 6f), Texture2D.whiteTexture);

        // 血量
        GUI.color = IsPhase2 ? new Color(0.95f, 0.35f, 0.3f) : new Color(0.85f, 0.25f, 0.25f);
        GUI.DrawTexture(new Rect(x, y, w * ratio, h), Texture2D.whiteTexture);

        // 阶段分界线
        GUI.color = new Color(1f, 1f, 1f, 0.35f);
        GUI.DrawTexture(new Rect(x + w * phase2Threshold, y, 2f, h), Texture2D.whiteTexture);

        GUI.color = Color.white;
    }
}
