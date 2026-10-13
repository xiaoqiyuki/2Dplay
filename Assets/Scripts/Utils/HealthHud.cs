using UnityEngine;

/// <summary>
/// 左上角的心形血量显示。
///
/// 【为什么不用图片资源】和 Greybox 一个思路：心形是运行时用一个字符画出来的，
/// 不需要往工程里放 PNG、不需要配导入设置、也不会因为"忘了设 Point 过滤"而糊掉。
/// 换样式只要改下面那 6 行字符串。
///
/// 【为什么不用 UI Canvas】加 Canvas + EventSystem + InputSystemUIInputModule
/// 是三个新的出错点。OnGUI 换来的代价只是样式朴素，对原型完全够用。
/// </summary>
public class HealthHud : MonoBehaviour
{
    // 心形图案。X = 实心，. = 透明。
    static readonly string[] HeartPattern =
    {
        ".XX.XX.",
        "XXXXXXX",
        "XXXXXXX",
        ".XXXXX.",
        "..XXX..",
        "...X...",
    };

    [Header("外观")]
    [Tooltip("单颗心在 640×360 基准分辨率下的尺寸（像素）。会按实际视口高度自动放大")]
    public int baseHeartWidth = 22;
    public int baseHeartHeight = 19;
    [Tooltip("心与心之间的间距（基准像素）")]
    public int baseSpacing = 4;
    [Tooltip("距离屏幕左上角的边距（基准像素）")]
    public int baseMarginX = 14;
    public int baseMarginY = 12;

    [Header("颜色")]
    public Color fullColor = new Color(0.95f, 0.28f, 0.36f, 1f);
    public Color emptyColor = new Color(0.25f, 0.22f, 0.28f, 0.85f);
    [Tooltip("受伤时整排心闪一下的颜色")]
    public Color damageFlashColor = new Color(1f, 1f, 1f, 1f);
    public float damageFlashTime = 0.18f;

    [Header("远程弹药")]
    public Color ammoFullColor = new Color(0.55f, 0.95f, 1f, 1f);
    public Color ammoEmptyColor = new Color(0.24f, 0.28f, 0.34f, 0.85f);

    Health _health;
    PlayerController _player;
    Texture2D _heart;
    int _lastHealth = -1;
    float _flashTimer;

    void Awake()
    {
        _heart = BuildHeartTexture();
    }

    void OnDestroy()
    {
        if (_heart != null) Destroy(_heart);
    }

    void Update()
    {
        // Health 是运行时加上去的，懒加载
        if (_health == null) _health = GetComponent<Health>();
        if (_health == null) return;

        if (_lastHealth < 0) _lastHealth = _health.currentHealth;

        // 掉血就闪一下（回血不闪）
        if (_health.currentHealth < _lastHealth) _flashTimer = damageFlashTime;
        _lastHealth = _health.currentHealth;

        if (_flashTimer > 0f) _flashTimer = Mathf.Max(_flashTimer - Time.unscaledDeltaTime, 0f);
    }

    void OnGUI()
    {
        if (_heart == null) return;
        if (_health == null) _health = GetComponent<Health>();
        if (_health == null || _health.maxHealth <= 0) return;

        // 按视口高度算缩放，这样 640×360 和 1920×1080 看起来一样大
        float s = Mathf.Max(1f, Screen.height / 360f);

        float w = baseHeartWidth * s;
        float h = baseHeartHeight * s;
        float gap = baseSpacing * s;
        float x0 = baseMarginX * s;
        float y0 = baseMarginY * s;

        int max = _health.maxHealth;
        int cur = Mathf.Clamp(_health.currentHealth, 0, max);

        for (int i = 0; i < max; i++)
        {
            bool filled = i < cur;

            Color c = filled ? fullColor : emptyColor;

            // 闪白：受伤瞬间整排亮一下
            if (_flashTimer > 0f && filled)
            {
                c = Color.Lerp(c, damageFlashColor, _flashTimer / damageFlashTime);
            }

            // 无敌帧期间让满心轻微闪烁，和角色闪烁呼应
            else if (filled && _health.HasDamageInvincibility)
            {
                float pulse = Mathf.PingPong(Time.unscaledTime * 8f, 1f);
                c = Color.Lerp(c, emptyColor, pulse * 0.45f);
            }

            GUI.color = c;
            GUI.DrawTexture(new Rect(x0 + i * (w + gap), y0, w, h), _heart);
        }

        GUI.color = Color.white;

        DrawAmmo(s, x0, y0 + h + 6f * s);
    }

    /// <summary>
    /// 远程弹药：画成排在心下面的小方块（"弹槽"）。
    /// 和心一个思路 —— 运行时生成的小贴图，不引资源。
    /// </summary>
    void DrawAmmo(float s, float x, float y)
    {
        if (_player == null) _player = GetComponent<PlayerController>();
        if (_player == null || _player.RangedMaxAmmo <= 0) return;

        int max = _player.RangedMaxAmmo;
        int cur = Mathf.Clamp(_player.RangedAmmo, 0, max);

        float w = 9f * s;
        float hh = 5f * s;
        float gap = 3f * s;

        for (int i = 0; i < max; i++)
        {
            bool filled = i < cur;
            GUI.color = filled ? ammoFullColor : ammoEmptyColor;
            GUI.DrawTexture(new Rect(x + i * (w + gap), y, w, hh), Texture2D.whiteTexture);
        }

        GUI.color = Color.white;
    }

    /// <summary>把字符图案变成一张 7×6 的白色贴图（白色是为了能用 GUI.color 随意染色）</summary>
    static Texture2D BuildHeartTexture()
    {
        int h = HeartPattern.Length;
        int w = HeartPattern[0].Length;

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // 贴图的 y 轴向上，字符串是自上而下写的，所以要翻过来
                bool on = HeartPattern[h - 1 - y][x] == 'X';
                tex.SetPixel(x, y, on ? Color.white : new Color(0f, 0f, 0f, 0f));
            }
        }

        tex.Apply();
        return tex;
    }
}
