using AgentPaw.Models;

namespace AgentPaw.Services;

/// <summary>
/// 빌트인 페르소나 그룹·템플릿 정의.
/// 모든 템플릿은 전역(project_id = NULL)으로 등록되며, 프로젝트에는 project_persona 링크로만 연결된다.
/// 시드 버전이 바뀌면 기존 빌트인을 모두 제거하고 재시드한다 (PersonaService.EnsureSeedAsync 참조).
/// </summary>
public static partial class PersonaDefaultsService
{
    /// <summary>시드 내용이 바뀔 때마다 이 값을 올려야 기존 설치본이 재시드된다.</summary>
    public const string SeedVersion = "2026.06.15.1";

    // === 그룹 키 ===
    private const string GPM = "grp_pm";
    private const string GAD = "grp_analysis_design";
    private const string GDA = "grp_data_ai";
    private const string GDV = "grp_development";
    private const string GOP = "grp_ops_infra";
    private const string GQS = "grp_quality_security";
    private const string GUX = "grp_ux_design";
    private const string GIL = "grp_illustration_art";
    private const string GWR = "grp_writing";
    private const string GVD = "grp_video_media";
    private const string GMK = "grp_marketing_biz";
    private const string GDC = "grp_docs_knowledge";
    private const string GRE = "grp_research_education";
    private const string GIA = "grp_investment_advisory";
    private const string GLG = "grp_legal";

    public static List<PersonaGroup> GetDefaultGroups()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new (string Id, string Name, string Desc, string Icon, int Order)[]
        {
            (GPM, "프로젝트 관리", "지시 수신·배정·보고 허브", "compass", 0),
            (GAD, "분석·설계", "요구·도메인·아키텍처 분석 계열", "drafting-compass", 10),
            (GDA, "데이터·AI", "데이터·머신러닝·프롬프트 계열", "database", 20),
            (GDV, "개발", "소프트웨어 구현 계열", "code", 30),
            (GOP, "운영·인프라", "배포·인프라·안정성 계열", "server", 40),
            (GQS, "품질·보안", "QA·테스트·보안 계열", "shield", 50),
            (GUX, "UX·디자인", "사용자 경험·인터페이스 디자인", "pen-tool", 60),
            (GIL, "일러스트·아트", "소설·웹툰·일반 일러스트 및 아트", "image", 70),
            (GWR, "문예·창작", "소설·시나리오·카피·편집", "book-open", 80),
            (GVD, "영상·미디어", "영상 기획·연출·편집·사운드", "film", 90),
            (GMK, "마케팅·비즈니스", "그로스·마케팅·세일즈·프로덕트", "megaphone", 100),
            (GDC, "문서·지식", "기술 문서·번역·지식 관리", "file-text", 110),
            (GRE, "연구·교육", "리서치·강의·멘토링", "graduation-cap", 120),
            (GIA, "투자·자문", "VC·AC·컨설턴트 투자 시뮬레이션", "line-chart", 130),
            (GLG, "법률·컴플라이언스", "계약·규제·법률 자문 계열", "scale", 135),
        };

        return entries.Select(e => new PersonaGroup
        {
            GroupId = e.Id,
            ProjectId = null,
            Name = e.Name,
            Description = e.Desc,
            Icon = e.Icon,
            SortOrder = e.Order,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
    }

    private static Persona Persona(
        string? projectId, string groupId,
        string name, string label, string description,
        string icon, string color, int sortOrder, bool isPm,
        string primaryModel, string fallbackModel, float temperature, int maxTokens,
        string systemPrompt, string keywords, string breedKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new Persona
        {
            PersonaId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            GroupId = groupId,
            Name = name,
            Label = label,
            Description = description,
            Icon = icon,
            Color = color,
            SortOrder = sortOrder,
            IsBuiltin = true,
            IsPm = isPm,
            PrimaryModel = primaryModel,
            FallbackModel = fallbackModel,
            Temperature = temperature,
            MaxTokens = maxTokens,
            SystemPrompt = systemPrompt,
            Keywords = keywords,
            Avatar = EngineAvatarService.GetBreedAvatar(breedKey),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>역할 키워드 기반 자동 품종 아바타 할당 (PNG 기반).</summary>
    public static string GetBreedAvatarForRole(string keywords)
        => EngineAvatarService.ResolveAvatarForPersona(null, keywords, isPm: false);
}
