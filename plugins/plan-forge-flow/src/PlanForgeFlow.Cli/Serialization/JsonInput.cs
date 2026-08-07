namespace PlanForgeFlow.Serialization;

internal static class JsonInput
{
    public static string Read(TextReader reader)
    {
        var input = reader.ReadToEnd();
        return input.Length > 0 && input[0] == '\uFEFF' ? input[1..] : input;
    }
}
