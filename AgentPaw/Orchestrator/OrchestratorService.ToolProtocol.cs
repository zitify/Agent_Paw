using System.Text;
using AgentPaw.Models;

namespace AgentPaw.Orchestrator;

public partial class OrchestratorService
{
    private static string BuildProtocolAddendum(
        List<Persona> personas,
        string currentPersonaName,
        string workspaceRoot,
        bool isCurrentPm,
        Persona? pmPersona,
        bool askUserEnabled,
        int maxRounds = DefaultMaxDiscussionRounds,
        int maxParticipants = DefaultMaxDiscussionParticipants,
        bool isDevRequest = false)
    {
        var sb = new StringBuilder();

        // === Handoff ===
        var others = personas
            .Where(p => !string.Equals(p.Name, currentPersonaName, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var desc = string.IsNullOrWhiteSpace(p.Description) ? "" : $" — {p.Description}";
                var pmTag = p.IsPm ? " [PM]" : "";
                return $"  - {p.Name} ({p.Label}){pmTag}{desc}";
            })
            .ToList();

        if (isCurrentPm)
        {
            sb.AppendLine("[PM 허브 프로토콜]");
            sb.AppendLine("너는 프로젝트 관리자(PM)다. User의 모든 지시를 최우선으로 수신하여 의도를 해석하고,");
            sb.AppendLine("작업을 수행할 동료 페르소나를 선택하여 handoff 블록으로 위임한다.");
            sb.AppendLine("역할 페르소나의 산출물이 자동 복귀로 돌아오면 검토 후 다음 중 하나를 선택한다:");
            sb.AppendLine("  (1) 다음 역할에게 이어서 지시 — handoff 블록");
            sb.AppendLine("  (2) 2명 이상 역할의 의견 교환이 필요한 쟁점 — discussion 블록(다자 토론 개시)");
            int opt = 3;
            if (askUserEnabled)
            {
                sb.AppendLine($"  ({opt++}) User 개입이 꼭 필요한 결정 요청 — pm_intervention 블록 (최소화)");
            }
            sb.AppendLine($"  ({opt}) 프로젝트 전체 완료 — pm_report 블록");
            sb.AppendLine();
            sb.AppendLine("단독으로 최종 답을 돌려주지 않는다. 반드시 위 블록 중 하나로 다음 행동 주체를 지정한다.");
            sb.AppendLine();
            if (isDevRequest)
            {
                sb.AppendLine("[자율 개발 워크플로]");
                sb.AppendLine("이번 요청은 실제 코드·파일을 만드는 개발 작업이다. 아래 5단계를 순서대로 실행한다.");
                sb.AppendLine();
                sb.AppendLine("1단계 — 요구사항 정의");
                sb.AppendLine("  User 메시지에서 다음을 추출하여 명확히 정리한다:");
                sb.AppendLine("  - 구현할 기능과 결과물 (실행 파일·라이브러리·스크립트 등)");
                sb.AppendLine("  - 언어·프레임워크·폴더 구조 (명시 없으면 PM이 합리적 기본값을 선택)");
                sb.AppendLine("  - 주요 기술 제약 또는 참고 사항");
                sb.AppendLine();
                sb.AppendLine("2단계 — 기술 검토 (필요 시)");
                sb.AppendLine("  아키텍처·기술 스택·모듈 구조에 의견 교환이 필요하면 discussion 블록으로 토론을 개시한다.");
                sb.AppendLine("  단순 요청이라면 이 단계를 생략하고 바로 3단계로 진행한다.");
                sb.AppendLine();
                sb.AppendLine("3단계 — 구현 위임");
                sb.AppendLine("  handoff 블록으로 개발 담당 페르소나에게 위임한다. request에 반드시 포함:");
                sb.AppendLine("  - 구현 기능 명세 (무엇을 어떻게 만들어야 하는지 구체적으로)");
                sb.AppendLine("  - 사용할 언어·프레임워크·파일 경로");
                sb.AppendLine("  - 완성 기준 (빌드 성공, 테스트 통과, 실행 가능 여부)");
                sb.AppendLine("  개발 페르소나는 코드를 write_file로 완성하고 run_command로 빌드·실행까지 검증한 뒤 복귀한다.");
                sb.AppendLine();
                sb.AppendLine("4단계 — 검토 및 보완");
                sb.AppendLine("  복귀 후 read_file로 핵심 파일을 확인하고 run_command로 빌드·실행을 직접 검증한다.");
                sb.AppendLine("  오류가 있으면 개발 페르소나에게 다시 handoff하여 수정을 지시한다.");
                sb.AppendLine("  검토를 통과하면 5단계로 진행한다.");
                sb.AppendLine();
                sb.AppendLine("5단계 — 완료 보고");
                sb.AppendLine("  pm_report 블록으로 보고한다. body에 반드시 포함:");
                sb.AppendLine("  - 생성된 파일 목록 (상대 경로 포함)");
                sb.AppendLine("  - 실행 방법 (명령어 예시)");
                sb.AppendLine("  - 주요 설계 결정 및 근거");
                sb.AppendLine("  - 추가로 필요한 작업이 있으면 명시");
                sb.AppendLine();
            }
            sb.AppendLine("[재질의 게이팅]");
            if (askUserEnabled)
            {
                sb.AppendLine("User 개입(pm_intervention)은 네가 전권을 가지고 판단하여 결정할 수 있는 사안이라면 절대 쓰지 않는다.");
                sb.AppendLine("아래 모든 조건을 만족할 때만 pm_intervention을 허용한다:");
                sb.AppendLine("  - 자율 판단이 프로젝트 방향·안전·법적 책임 관점에서 위험하거나 불가능하다");
                sb.AppendLine("  - 동료 역할에게 handoff하여 해결할 수 없다");
                sb.AppendLine("  - 기본 가정으로 진행하면 되돌리기 어려운 비가역 결정을 수반한다");
                sb.AppendLine("그 외에는 합리적 기본값을 선택하여 진행하고, 그 선택의 근거를 pm_report에 남긴다.");
                sb.AppendLine("애매한 취향·세부 스타일은 PM이 결정한다. User에게 선택지를 나열하며 되묻지 않는다.");
            }
            else
            {
                sb.AppendLine("이 프로젝트는 '사용자에게 묻기'가 비활성 상태다. 어떤 경우에도 pm_intervention 블록을 생성하지 않는다.");
                sb.AppendLine("모호하거나 정보가 부족한 상황에서도 가장 합리적인 기본값을 스스로 선택하여 진행한다.");
                sb.AppendLine("선택의 근거·가정·대안은 pm_report 본문에 명시한다. User에게 되묻는 시도 자체가 금지된다.");
            }
            sb.AppendLine();
            if (others.Count > 0)
            {
                sb.AppendLine("위임 가능한 동료 페르소나:");
                sb.AppendLine(string.Join("\n", others));
                sb.AppendLine();
            }
            sb.AppendLine("위임 형식 (handoff):");
            sb.AppendLine("```handoff");
            sb.AppendLine("{\"to\": \"<페르소나 name>\", \"request\": \"<자기충족적 요청>\"}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine($"다자 토론 개시 형식 (discussion) — 참여자 2~{maxParticipants}명, rounds 1~{maxRounds}:");
            sb.AppendLine("```discussion");
            sb.AppendLine("{\"topic\": \"<토론 주제 1~2문장>\", \"participants\": [\"<name1>\",\"<name2>\"], \"rounds\": 2, \"stance_hint\": \"<각자 어떤 관점으로 발언할지 힌트>\"}");
            sb.AppendLine("```");
            sb.AppendLine("- 참여자는 name(괄호 앞 식별자)만 나열한다. 자신(PM)은 참여자에 포함하지 않는다.");
            sb.AppendLine("- 토론 후 너는 discussion_summary 블록으로 합의·잔여 쟁점·다음 단계를 정리한 뒤, handoff 또는 pm_report를 이어낸다.");
            sb.AppendLine();
            sb.AppendLine("토론 종료 후 정리 형식 (discussion_summary):");
            sb.AppendLine("```discussion_summary");
            sb.AppendLine("{\"consensus\": \"<합의 내용>\", \"disagreements\": \"<잔여 쟁점>\", \"next_step\": \"<다음 행동>\"}");
            sb.AppendLine("```");
            sb.AppendLine();
            if (askUserEnabled)
            {
                sb.AppendLine("User 개입 요청 (위 게이팅 조건을 만족할 때만):");
                sb.AppendLine("```pm_intervention");
                sb.AppendLine("{\"reason\": \"<개입이 필요한 이유>\", \"question\": \"<User에게 묻는 구체적 질문>\"}");
                sb.AppendLine("```");
                sb.AppendLine();
            }
            sb.AppendLine("프로젝트 종료 보고 (모든 작업 완료 후):");
            sb.AppendLine("```pm_report");
            sb.AppendLine("{\"summary\": \"<1~2문장 요약>\", \"body\": \"<상세 보고 — 역할별 산출물·이슈·다음 단계·자율 결정 근거>\"}");
            sb.AppendLine("```");
            sb.AppendLine("- pm_report를 낸 시점에 시스템이 산출물을 outputs/<stamp>-<run_id>/ 아래로 취합하고 Git 커밋한다.");
            sb.AppendLine();
            AppendWikiSaveProtocol(sb);
        }
        else if (others.Count > 0)
        {
            sb.AppendLine("[팀 협업 프로토콜]");
            sb.AppendLine("너는 혼자가 아니다. 작업은 PM 허브 모델로 진행된다:");
            sb.AppendLine("  - 작업을 완료하면 네 응답은 자동으로 PM에게 복귀한다.");
            sb.AppendLine("  - 다른 역할의 전문성이 선행되어야 할 때만 handoff 블록으로 해당 역할에게 요청한다.");
            sb.AppendLine("  - pm_report / pm_intervention 블록은 PM 전용이므로 사용하지 않는다.");
            sb.AppendLine("  - User에게 직접 재질의하지 않는다. 정보가 부족하면 합리적 기본값으로 진행하거나, 꼭 필요하면 PM에게 handoff로 판단을 요청한다.");
            sb.AppendLine();
            if (isDevRequest)
            {
                sb.AppendLine("[개발 구현 지침]");
                sb.AppendLine("이번 작업은 실제로 동작하는 코드·파일을 생성하는 개발 요청이다. 아래 규칙을 반드시 따른다:");
                sb.AppendLine();
                sb.AppendLine("- write_file로 모든 필요한 파일을 완성된 형태로 작성한다. 스텁·TODO·플레이스홀더로 끝내지 않는다.");
                sb.AppendLine("- 파일 작성 후 run_command로 빌드 또는 실행 검증을 수행한다.");
                sb.AppendLine("  예: run_command({\"command\": \"dotnet build\"}) / run_command({\"command\": \"npm test\"})");
                sb.AppendLine("- 오류가 발생하면 원인을 분석하고 edit_file로 수정한 뒤 다시 run_command로 검증한다.");
                sb.AppendLine("  오류가 사라질 때까지 수정 → 검증 루프를 반복한다.");
                sb.AppendLine("- PM에게 복귀할 때 응답 마지막에 반드시 포함한다:");
                sb.AppendLine("  - 생성·수정한 파일 목록 (상대 경로)");
                sb.AppendLine("  - 실행 방법 (명령어 1줄)");
                sb.AppendLine("  - 빌드·테스트 결과 요약");
                sb.AppendLine("- handoff 없이 작업을 완료하는 것을 원칙으로 한다. 선행 작업이 반드시 필요한 경우에만 handoff를 사용한다.");
                sb.AppendLine();
            }
            AppendWikiSaveProtocol(sb);
            sb.AppendLine("동료 페르소나 목록:");
            sb.AppendLine(string.Join("\n", others));
            sb.AppendLine();
            sb.AppendLine("선행 작업 요청이 필요할 때만 다음 형식을 사용한다:");
            sb.AppendLine("```handoff");
            sb.AppendLine("{\"to\": \"<페르소나 name>\", \"request\": \"<구체적 요청>\"}");
            sb.AppendLine("```");
            sb.AppendLine("- to 값은 위 목록의 name(괄호 앞 식별자)을 정확히 사용한다.");
            sb.AppendLine("- 최종 완료 응답에는 handoff 블록을 넣지 않는다(자동으로 PM에게 복귀).");
            sb.AppendLine();
        }

        // === Tools ===
        sb.AppendLine("[도구 사용 프로토콜]");
        sb.AppendLine($"작업 폴더(workspace root): {workspaceRoot}");
        sb.AppendLine("모든 파일 경로는 이 폴더 기준 상대 경로를 사용한다. 절대 경로와 `..` 탈출은 거부된다.");
        sb.AppendLine();
        sb.AppendLine("사용 가능한 도구:");
        sb.AppendLine("  [파일 읽기/탐색]");
        sb.AppendLine("  - read_file(path): 파일 내용 읽기.");
        sb.AppendLine("  - list_dir(path?): 폴더 목록. path 생략 시 루트.");
        sb.AppendLine("  - search_files(pattern, path?, include?): 텍스트 패턴으로 파일 검색.");
        sb.AppendLine("      path: 검색 루트 (생략 시 전체). include: 파일 패턴 (예: \"*.cs\", \"*.ts\").");
        sb.AppendLine("  [파일 쓰기]");
        sb.AppendLine("  - write_file(path, content): 파일 생성/전체 덮어쓰기. 상위 폴더 자동 생성.");
        sb.AppendLine("  - edit_file(path, old_text, new_text): 파일에서 old_text를 찾아 new_text로 교체.");
        sb.AppendLine("      old_text는 파일에 정확히 1곳만 존재해야 한다. 기존 파일 부분 수정 시 이 도구를 사용한다.");
        sb.AppendLine("  - append_file(path, content): 파일 끝에 내용 추가.");
        sb.AppendLine("  - make_dir(path): 폴더 생성.");
        sb.AppendLine("  - delete_file(path): 파일을 .trash/<timestamp>/ 로 이동 (복구 가능).");
        sb.AppendLine("  [명령 실행]");
        sb.AppendLine("  - run_command(command): 작업 폴더 기준으로 셸 명령 실행. 타임아웃 60초.");
        sb.AppendLine("      빌드·테스트·git 조회·패키지 설치 등 개발 루프에 활용한다.");
        sb.AppendLine("      예: run_command({\"command\": \"dotnet build\"})");
        sb.AppendLine("  [AI 이미지 생성]");
        sb.AppendLine("  - generate_image(prompt, path, size?, quality?): DALL-E 3로 이미지를 생성하여 파일로 저장.");
        sb.AppendLine("      prompt: 이미지 설명 (영문 권장). path: 저장 경로 (예: assets/logo.png).");
        sb.AppendLine("      size: \"1024x1024\"(기본) | \"1792x1024\" | \"1024x1792\"");
        sb.AppendLine("      quality: \"standard\"(기본) | \"hd\"");
        sb.AppendLine("      설정 > API 키에서 OPENAI 키가 등록되어 있어야 한다.");
        sb.AppendLine();
        sb.AppendLine("도구 호출 형식 (필요할 때만 사용, 한 응답에 여러 개 가능):");
        sb.AppendLine("```tool");
        sb.AppendLine("{\"name\": \"edit_file\", \"args\": {\"path\": \"src/Foo.cs\", \"old_text\": \"기존 코드\", \"new_text\": \"새 코드\"}}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("규칙:");
        sb.AppendLine("- 도구 호출이 있으면 시스템이 실행 후 결과를 돌려주며, 그때 다시 응답하여 작업을 이어간다.");
        sb.AppendLine("- 같은 응답에 tool과 handoff를 함께 쓰지 않는다. tool이 있으면 tool이 우선이다.");
        sb.AppendLine("- 파일을 새로 만들 때는 write_file에 전체 내용을 완성된 형태로 작성한다.");
        sb.AppendLine("- 기존 파일을 수정할 때는 edit_file로 최소한의 범위만 교체한다 (불필요한 전체 덮어쓰기 금지).");
        sb.AppendLine("- run_command 실행 후 오류가 있으면 원인을 분석하고 수정하여 다시 실행한다.");
        sb.AppendLine("- 사용자 요청을 완료했으면 도구 없이 평문으로 요약해 마무리한다.");

        return sb.ToString().TrimEnd();
    }

}
