using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 音效 / BGM 播放器。整个游戏只有这一个音频入口。
///
/// 【设计原则：调用方永远不用判空】
/// 所有播放接口都是 static，而且：
///   · 管理器不存在（场景里没挂）→ 静默什么都不做
///   · 音频文件不存在 → 只警告一次，不会每帧刷屏
///   · 多个音效同时响 → PlayOneShot 自己会叠
/// 这样往任何地方插一句 AudioManager.Play("sfx_jump") 都不会引入新的崩溃点，
/// 也不会因为"忘了配 AudioSource"而报 NullReference。
///
/// 【资源从哪来】Resources.Load("Audio/xxx")。
/// 占位音效由 build/make_audio.py 合成，放在 Assets/Resources/Audio/。
/// W9 换正式音频时，保持文件名不变直接替换即可，代码一行都不用改。
/// </summary>
public class AudioManager : MonoBehaviour
{
    // 预加载清单。漏了也能用（Play 时会按需加载），列在这里只是为了开局不卡。
    static readonly string[] Preload =
    {
        "sfx_jump", "sfx_double_jump", "sfx_dash", "sfx_land",
        "sfx_swing", "sfx_hit", "sfx_parry", "sfx_hurt",
        "sfx_death", "sfx_enemy_death", "sfx_charge", "sfx_slam",
        "sfx_shockwave", "sfx_checkpoint", "sfx_victory", "sfx_ui",
    };

    [Header("音量")]
    [Range(0f, 1f)] public float sfxVolume = 0.75f;
    [Range(0f, 1f)] public float bgmVolume = 0.30f;
    [Tooltip("静音开关。做试玩调试时很有用")]
    public bool mute = false;

    [Header("BGM")]
    public string bgmClipName = "bgm_loop";
    public bool playBgmOnStart = true;

    [Header("音效通道")]
    [Tooltip("同时能播多少个音效。用【池】是必须的 —— 见下面 PlayInternal 的说明。\n" +
             "16 个声道开销可以忽略，不要为了省内存调到 1。")]
    public int sfxVoices = 16;
    [Tooltip("勾上则每次因为池满而顶掉一个声音时打日志。\n" +
             "只在真的发生时才打，所以平时不会有噪音 —— 建议开着")]
    public bool logVoiceStealing = true;

    public static AudioManager Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
    }

    bool _bgmDucked;
    AudioSource _bgmSource;
    AudioSource[] _sfxPool;
    int _poolCursor;
    int _stealCount;
    readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();
    readonly HashSet<string> _warned = new HashSet<string>();

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // ---- 音效通道池 ----
        // 【为什么不能只用一个 AudioSource】
        // pitch 是 AudioSource 的属性，不是每个音效自己的。用一个 source 时，
        // 后触发的音效改了 pitch，正在播的前一个也会被一起变速 ——
        // 听起来就是"前一个音效被顶了一下"。PlayOneShot 本身不会打断它（会混音），
        // 但音高是共享的，这个坑很隐蔽。
        // 所以每个音效独占一个 source，pitch 互不干扰。
        int voices = Mathf.Clamp(sfxVoices, 1, 64);
        _sfxPool = new AudioSource[voices];
        for (int i = 0; i < voices; i++)
        {
            AudioSource src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;      // 2D
            src.volume = 1f;
            src.priority = 64;          // 比 BGM 低，声部不够时先牺牲音效
            _sfxPool[i] = src;
        }

        _bgmSource = gameObject.AddComponent<AudioSource>();
        _bgmSource.playOnAwake = false;
        _bgmSource.spatialBlend = 0f;
        _bgmSource.loop = true;
        _bgmSource.volume = bgmVolume;
        _bgmSource.priority = 0;        // BGM 优先级最高，绝不被顶掉

        CheckAudioListener();

        for (int i = 0; i < Preload.Length; i++) Load(Preload[i]);

        Debug.Log(string.Format("[AudioManager] 就绪：{0} 个音效通道，预加载 {1} 个音效，BGM = {2}",
                  voices, Preload.Length, bgmClipName), this);
    }

    /// <summary>
    /// 场景里必须【有且只有一个】AudioListener。
    /// 0 个 = 全静音；2 个以上 = Unity 会警告并且只有其中一个生效，
    /// 表现常常是"某些声音莫名其妙听不到"，很难查。
    /// </summary>
    void CheckAudioListener()
    {
        AudioListener[] listeners = FindObjectsByType<AudioListener>(FindObjectsSortMode.None);

        if (listeners.Length == 1) return;

        if (listeners.Length == 0)
        {
            Debug.LogError("[AudioManager] 场景里一个 AudioListener 都没有 —— 会完全没声音。\n" +
                           "修法：给 Main Camera 加一个 Audio Listener 组件。", this);
        }
        else
        {
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < listeners.Length; i++)
            {
                if (i > 0) names.Append(", ");
                names.Append(listeners[i].gameObject.name);
            }
            Debug.LogWarning(string.Format(
                "[AudioManager] 场景里有 {0} 个 AudioListener（{1}）—— 只会有其中一个生效，" +
                "表现可能是「某些声音听不到」。\n" +
                "留 Main Camera 上的那一个，把其余物体上的 Audio Listener 删掉。",
                listeners.Length, names.ToString()), this);
        }
    }

    void Start()
    {
        if (playBgmOnStart) PlayBgm();
    }

    AudioClip Load(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        AudioClip c;
        if (_cache.TryGetValue(name, out c)) return c;

        c = Resources.Load<AudioClip>("Audio/" + name);
        _cache[name] = c;

        if (c == null && !_warned.Contains(name))
        {
            _warned.Add(name);
            Debug.LogWarning(string.Format(
                "[AudioManager] 找不到音频 Audio/{0}。\n" +
                "确认文件在 Assets/Resources/Audio/{0}.wav（占位音效由 build/make_audio.py 生成）。\n" +
                "缺一个音效不会影响玩法，只是那一下没声音。", name), this);
        }

        return c;
    }

    // ---------------------------------------------------------------- 对外接口

    /// <summary>放一个音效。pitchJitter 让连续触发的同一个音效不至于像机关枪。</summary>
    public static void Play(string name, float volume = 1f, float pitchJitter = 0.06f)
    {
        if (Instance == null) return;
        Instance.PlayInternal(name, volume, pitchJitter);
    }

    /// <summary>放一个固定音高的音效（比如 UI 点击，不需要抖动）。</summary>
    public static void PlayFlat(string name, float volume = 1f)
    {
        Play(name, volume, 0f);
    }

    public static void PlayBgm()
    {
        if (Instance == null) return;
        Instance.PlayBgmInternal();
    }

    public static void StopBgm()
    {
        if (Instance == null || Instance._bgmSource == null) return;
        Instance._bgmSource.Stop();
    }

    /// <summary>暂停时把 BGM 音量压下去 —— Time.timeScale 不会影响音频</summary>
    public static void SetBgmDucked(bool ducked)
    {
        if (Instance == null) return;
        Instance._bgmDucked = ducked;
        Instance.ApplyBgmVolume();
    }

    void ApplyBgmVolume()
    {
        if (_bgmSource == null) return;
        _bgmSource.volume = mute ? 0f : (_bgmDucked ? bgmVolume * 0.35f : bgmVolume);
    }

    // ---------------------------------------------------------------- 内部

    void PlayInternal(string name, float volume, float pitchJitter)
    {
        if (mute || _sfxPool == null || _sfxPool.Length == 0) return;

        AudioClip clip = Load(name);
        if (clip == null) return;

        AudioSource src = TakeSource();
        if (src == null) return;

        // 池里的每个 source 是独占的，所以这里改 pitch 只会影响这一个音效，
        // 不会把正在播的其他音效一起变速。
        src.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
        src.PlayOneShot(clip, Mathf.Clamp01(volume) * sfxVolume);
    }

    /// <summary>
    /// 从池里取一个空闲声道。全都在播的时候才轮到"顶掉"：
    /// 按游标轮转，顶掉最久以前启用的那个。
    /// 注意这是【池满】时的兜底，不是常态 —— 16 个声道下正常情况下不会发生。
    /// </summary>
    AudioSource TakeSource()
    {
        int n = _sfxPool.Length;

        for (int i = 0; i < n; i++)
        {
            int idx = (_poolCursor + i) % n;
            if (!_sfxPool[idx].isPlaying)
            {
                _poolCursor = (idx + 1) % n;
                return _sfxPool[idx];
            }
        }

        // 池满了：顶掉一个
        AudioSource stolen = _sfxPool[_poolCursor];
        _poolCursor = (_poolCursor + 1) % n;
        _stealCount++;

        if (logVoiceStealing)
        {
            Debug.LogWarning(string.Format(
                "[AudioManager] 音效通道用满了（{0} 个），顶掉了正在播的 {1}。累计 {2} 次。\n" +
                "如果频繁出现，把 Sfx Voices 调大，或者检查是不是有音效在被每帧触发。",
                n, stolen.clip != null ? stolen.clip.name : "(未知)", _stealCount), this);
        }

        return stolen;
    }

    void PlayBgmInternal()
    {
        if (_bgmSource == null) return;

        AudioClip clip = Load(bgmClipName);
        if (clip == null) return;

        if (_bgmSource.isPlaying && _bgmSource.clip == clip) return;

        _bgmSource.clip = clip;
        // 用 ApplyBgmVolume 而不是直接赋值：GameFlow 的 Start() 可能比这里的 Start() 先跑，
        // 那时它已经把"暂停压低"设好了，直接写 bgmVolume 会把压低状态冲掉。
        ApplyBgmVolume();
        _bgmSource.Play();
    }
}
