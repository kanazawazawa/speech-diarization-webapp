namespace Speech2Text.Models;

public class TranscriptionItem
{
    public string Text { get; set; } = string.Empty;
    public string SpeakerId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsFinalized { get; set; } = true;
    public bool IsNote { get; set; } = false;
}
