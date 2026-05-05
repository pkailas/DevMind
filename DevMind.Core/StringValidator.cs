namespace DevMind
{
    public static class StringValidator
    {
        public static bool IsValid(string input) => !string.IsNullOrWhiteSpace(input);
    }
}