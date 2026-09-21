using System.Text;
using PlanForge.Review;

namespace PlanForge.Acts;

internal static class BuilderBrief
{
    public static string Prepend(string prompt, string plan)
    {
        var brief = PlanTasks.Brief(plan);
        if (string.IsNullOrWhiteSpace(brief)) return prompt;

        SensitiveInput.Guard(brief, "the Builder Brief");
        return new StringBuilder().AppendLine("# Builder Brief")
                                  .AppendLine()
                                  .Append(brief)
                                  .Append(prompt)
                                  .ToString();
    }
}
