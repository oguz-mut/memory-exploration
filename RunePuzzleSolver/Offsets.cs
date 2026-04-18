namespace RunePuzzleSolver;

static class Offsets
{
    public static class RunePuzzleSubController
    {
        public const int PuzzleWindow = 0x20;
        public const int PuzzleRunes = 0x28;
        public const int SubmitButton = 0x38;
        public const int InputDisplayContainer = 0x40;
        public const int InputDisplayFieldTemplate = 0x48;
        public const int GuessContainer = 0x50;
        public const int GuessTemplate = 0x58;
        public const int EntityIdInteractor = 0x60;
        public const int CodeLength = 0x64;
        public const int NumGuessesAllowed = 0x68;
        public const int InputDisplayButtons = 0x70;
        public const int CurGuess = 0x78;
    }

    public static class UIRunePuzzleGuessRow
    {
        public const int Runes = 0x20;
        public const int Text = 0x28;
    }

    public static class GameObject
    {
        public const int ActiveSelf = 0x19; // bool byte
    }

    public static class TextMeshProUGUI
    {
        public const int TextStr = 0x90; // string*
    }

    public static class IL2CppString
    {
        public const int Length = 0x10;
        public const int Chars = 0x14; // UTF-16
    }

    public static class IL2CppList
    {
        public const int Items = 0x10; // array*
        public const int Size = 0x18;  // int
    }

    public static class IL2CppArray
    {
        public const int Length = 0x18;
        public const int FirstElem = 0x20;
    }

    public const ulong MinValidPtr = 0x10000UL;
}
