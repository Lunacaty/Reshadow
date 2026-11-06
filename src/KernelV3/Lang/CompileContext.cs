using System;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

/// <summary>
/// 「编译期」与「运行时」共用同一份符号表。
///
/// 这是整个引擎最容易踩的坑，也是唯一一条必须显式记住的约束：
///   <c>SymbolId&lt;T&gt;</c> 的整数 Id 由 <c>GameSchema</c> 实例在注册时分配。
///   编译器用它自己的 Lexicon.Schema 给 "妖精" 分配 Id 0，
///   运行时的 BattleState 用另一个 Schema 分配 Id 1 —— 同一个兵种名字，
///   两个 Id，<c>Equals</c> 为 false。
///
/// 后果是静默的：AST 打印正常、编译零错误，但
/// 「每当一个 Pixie 随从进场」在运行时永远匹配不上，
/// 因为 <c>HasTraitExpr</c> 手里的 Id 与实体身上那个 Id 不是一回事。
///
/// 正确用法：
/// <code>
///   var ctx  = CompileContext.Create(seed: 1);
///   var defs = ctx.Compile(text, altModes, cardName);   // 用运行时的 Schema 编译
///   ctx.State.RegisterCard(...);
/// </code>
/// </summary>
public sealed class CompileContext
{
    public Battle.BattleState State { get; }
    public GameSchema Schema { get; }
    public Lexicon Lexicon { get; }

    private CompileContext(Battle.BattleState state)
    {
        State = state;
        Schema = state.Schema;
        // 关键：Lexicon 复用运行时的 Schema，符号 Id 从此只有一个来源。
        Lexicon = new Lexicon(Schema);
    }

    /// <summary>建一个「编译期与运行时共用符号表」的上下文。</summary>
    public static CompileContext Create(int seed = 1) => new(new Battle.BattleState(seed: seed));

    /// <summary>接管一个已存在的 BattleState（它自己的 Schema 成为唯一真相源）。</summary>
    public static CompileContext For(Battle.BattleState state) => new(state);

    /// <summary>注册关键词（同 Schema）。</summary>
    public CompileContext Keywords(params string[] names)
    {
        foreach (var n in names) if (!string.IsNullOrWhiteSpace(n)) Lexicon.AddKeyword(n);
        return this;
    }

    /// <summary>注册兵种（同 Schema）。</summary>
    public CompileContext Traits(params string[] names)
    {
        foreach (var n in names) if (!string.IsNullOrWhiteSpace(n)) Lexicon.AddTrait(n);
        return this;
    }

    /// <summary>注册卡名（词法层认得出「add a X to your hand」里的 X）。</summary>
    public CompileContext Cards(params (string name, int id)[] cards)
    {
        foreach (var (n, i) in cards) if (!string.IsNullOrWhiteSpace(n)) Lexicon.AddCard(n, i);
        return this;
    }

    /// <summary>构建词法表。加完所有词之后必须调用一次。</summary>
    public CompileContext Build()
    {
        Lexicon.Build();
        return this;
    }

    /// <summary>用运行时的 Schema 编译一段卡面文本。</summary>
    public EnglishCompiler.Result Compile(
        string text,
        (string typeEng, int cost, string textEng)[] altModes = null,
        string cardName = null,
        int cardId = 0)
        => new EnglishCompiler(Lexicon).Compile(text, altModes, cardName, cardId);
}
