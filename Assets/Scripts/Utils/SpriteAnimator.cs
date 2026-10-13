using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 极简换帧动画。不用 Unity 的 Animator ——
/// 那需要 Animator Controller、状态机、过渡条件三样资源，对"几帧循环"来说太重了，
/// 而且每加一个角色就多一处配错的机会。这里就是一个数组 + 计时器。
///
/// 【两种驱动方式】
///   1. 外部调 Play("run")            —— 给敌人 / Boss 用
///   2. driveFromPlayerState = true   —— 自动读 PlayerController.CurrentState
///      这样 PlayerController 一行都不用改，动画和运动逻辑不会互相纠缠
///
/// 【帧从哪来】Resources.Load，一个文件一帧（见 build/make_characters.py 的说明：
/// 精灵表要手动切格，切格信息会随文件名变化失效 —— 一个文件一帧就不用切）。
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class SpriteAnimator : MonoBehaviour
{
    [Tooltip("勾上则根据 PlayerController.CurrentState 自动切动画")]
    public bool driveFromPlayerState = false;

    [Tooltip("没有对应动画时用哪个")]
    public string defaultClip = "idle";

    class Clip
    {
        public Sprite[] frames;
        public float fps;
        public bool loop;
    }

    SpriteRenderer _sr;
    PlayerController _player;
    readonly Dictionary<string, Clip> _clips = new Dictionary<string, Clip>();
    readonly List<string> _order = new List<string>();

    string _current;
    float _timer;
    int _frame;
    bool _finished;

    void Awake()
    {
        _sr = GetComponent<SpriteRenderer>();
        if (driveFromPlayerState) _player = GetComponentInParent<PlayerController>();
    }

    /// <summary>加一段动画。</summary>
    public void AddClip(string name, float fps, bool loop, params Sprite[] frames)
    {
        if (string.IsNullOrEmpty(name) || frames == null || frames.Length == 0) return;

        // 过滤掉加载失败的帧（美术没到位时不应该整段动画都不播）
        var valid = new List<Sprite>();
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i] != null) valid.Add(frames[i]);
        }
        if (valid.Count == 0) return;

        _clips[name] = new Clip
        {
            frames = valid.ToArray(),
            fps = Mathf.Max(1f, fps),
            loop = loop,
        };
        _order.Add(name);

        if (_current == null) Play(name, true);
    }

    /// <summary>按名字播。已经在播的不会重头开始（除非 forceRestart）。</summary>
    public void Play(string name, bool forceRestart = false)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!_clips.ContainsKey(name)) return;
        if (_current == name && !forceRestart && !_finished) return;

        _current = name;
        _frame = 0;
        _timer = 0f;
        _finished = false;
        Apply();
    }

    /// <summary>加载一组按序号命名的帧：prefix + "0".."count-1"</summary>
    public static Sprite[] LoadFrames(string folder, string prefix, int count)
    {
        var list = new List<Sprite>();
        for (int i = 0; i < count; i++)
        {
            string path = folder + "/" + prefix + i;
            Sprite s = Resources.Load<Sprite>(path);
            if (s != null) list.Add(s);
        }
        return list.ToArray();
    }

    void Update()
    {
        if (driveFromPlayerState && _player != null)
        {
            Play(ClipForState(_player.CurrentState));
        }

        Clip clip;
        if (_current == null || !_clips.TryGetValue(_current, out clip)) return;
        if (clip.frames.Length <= 1) return;
        if (_finished) return;

        _timer += Time.deltaTime;
        float step = 1f / clip.fps;

        while (_timer >= step)
        {
            _timer -= step;

            if (_frame + 1 < clip.frames.Length)
            {
                _frame++;
            }
            else if (clip.loop)
            {
                _frame = 0;
            }
            else
            {
                _finished = true;   // 非循环动画停在最后一帧
                break;
            }

            Apply();
        }
    }

    void Apply()
    {
        Clip clip;
        if (_current == null || !_clips.TryGetValue(_current, out clip)) return;
        if (_sr == null || clip.frames.Length == 0) return;

        _frame = Mathf.Clamp(_frame, 0, clip.frames.Length - 1);
        _sr.sprite = clip.frames[_frame];
    }

    /// <summary>
    /// 玩家状态 → 动画名。名字要和 AddClip 里注册的一致。
    /// 状态枚举是 HUD 用的那个（Idle/Run/Jump/Fall/Dash/Hurt），这里只做映射，不改逻辑。
    /// </summary>
    static string ClipForState(PlayerController.State s)
    {
        switch (s)
        {
            case PlayerController.State.Run: return "run";
            case PlayerController.State.Jump: return "jump";
            case PlayerController.State.Fall: return "fall";
            case PlayerController.State.Dash: return "dash";
            case PlayerController.State.Hurt: return "hurt";
            default: return "idle";
        }
    }
}
