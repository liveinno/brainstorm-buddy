namespace BrainstormBuddy.Config;

public class UserProfile
{
    public string Summary { get; set; } = "";
    public List<string> Cases { get; set; } = new();
    public List<string> TechnicalSkills { get; set; } = new();
    public List<string> SoftSkills { get; set; } = new();
    public string CannotDo { get; set; } = "";

    // ДЕМО-РЕЗЮМЕ: полностью вымышленный персонаж (компании/цифры выдуманы) — образец для
    // пользователя. Реальные данные сюда не помещать: этот файл уходит в публичную сборку.
    public static UserProfile CreateDefault() => new()
    {
        Summary = "Руководитель ИТ проектов, 14+ лет опыта. Специализация: управление инфраструктурными платформами, заказная разработка enterprise систем, B2B продукты. Бюджеты проектов 10-95 млн руб.",
        Cases = new()
        {
            "EAM система ТОиР (НордОйл, нефтегаз): бюджет 85 млн руб, команда 15 чел. Автоматизация регресс тестов через AI: 200+ unit тестов, время тестирования с 5 дней до 4 часов.",
            "Система мониторинга добычи (НордОйл): распределённый запуск без критических дефектов. 30+ скриптов автоматизации (PowerShell, Groovy) для клонирования конфигураций.",
            "B2B продукт СКУД (СвязьТелеком): запуск нового продукта за 12 месяцев. 240+ лидов в контракты. Интеграция с SAP, CRM, BSS, BPMS.",
            "Восстановление слаботочных систем (Технопарк «Заречье»): 10 объектов за 18 месяцев. Запуск ServiceDesk с SLA, проекты компьютерного зрения.",
            "RAG системы: корпоративные базы знаний (PrivateGPT + Qdrant/AnythingLLM) для работы с ТЗ, ГОСТ, договорами."
        },
        TechnicalSkills = new()
        {
            "Agile, Scrum, Kanban, BPMN, UML", "Jira, Confluence, MS Project, Miro", "Git, SQL, API, CI/CD, OKR",
            "Linux, Windows Server, TCP/IP, VLAN, VPN", "SAP, CRM, BSS, BPMS, 1С",
            "PowerShell, Groovy, Python, RAG (PrivateGPT, Qdrant, AnythingLLM)",
            "AI tools (Cursor, Copilot, Claude, Windsurf, Roo code, Cline)",
            "Bosch VMS, Рубеж, Болид, Интеллект, RTSP, ONVIF"
        },
        SoftSkills = new()
        {
            "Управление командой, Переговоры, Коммуникация", "Бизнес-анализ, Планирование, Управление проектами",
            "Постановка задач разработчикам, Управление бэклогом, Приоритизация",
            "Формирование ТЗ, Управление продуктом, Управление рисками, Управление подрядчиками"
        },
        CannotDo = "Не работал с блокчейном. Не внедрял AI/ML модели в продакшн. Слаб в маркетинге и продажах. Не разрабатывал мобильные приложения. Не сертифицирован PMP."
    };

    public string FormatForPrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## РЕЗЮМЕ КАНДИДАТА — все факты, числа, названия проектов бери ТОЛЬКО отсюда.");
        sb.AppendLine("Если вопрос не про опыт из этого резюме — не притягивай проект за уши, отвечай коротко и честно либо промолчи.");
        sb.AppendLine($"Резюме: {Summary}");
        sb.AppendLine();
        sb.AppendLine("### Ключевые кейсы:");
        foreach (var c in Cases) sb.AppendLine($"- {c}");
        sb.AppendLine();
        sb.AppendLine($"### Технические навыки: {string.Join(", ", TechnicalSkills)}");
        sb.AppendLine($"### Soft skills: {string.Join(", ", SoftSkills)}");
        sb.AppendLine($"### Не умею / избегать: {CannotDo}");
        return sb.ToString();
    }
}