using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2050: <see cref="ICCardManager.Services.TemplateResolver"/> の一時テンプレートを扱うテストのコレクション定義。
/// </summary>
/// <remarks>
/// <see cref="ICCardManager.Services.TemplateResolver.CleanupTempFiles"/> は %TEMP%\ICCardManager の
/// <c>ICCardManager_Template_*.xlsx</c> をすべて削除する。展開した一時テンプレートを直接読むテストと
/// 並列に走ると、読んでいる途中のファイルが消えて偶発的に失敗するため、
/// <c>CleanupTempFiles</c> を呼ぶテストと一時テンプレートを読むテストはこのコレクションに属させる。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class TemplateTempFileCollection
{
    public const string Name = "TemplateResolver Temp Files";
}
