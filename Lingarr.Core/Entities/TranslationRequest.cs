using System.ComponentModel.DataAnnotations.Schema;
using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

public class TranslationRequest : BaseEntity
{
    public string? JobId  { get; set; }
    public TranslationJobType? JobType { get; set; }
    public int? MediaId  { get; set; }
    public required string Title { get; set; }
    public required string SourceLanguage { get; set; }
    public required string TargetLanguage { get; set; }
    public string? SubtitleToTranslate { get; set; }
    public string? TranslatedSubtitle { get; set; }
    public required MediaType MediaType { get; set; }
    public required TranslationStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    [Column("failed_positions")]
    public string? FailedPositionsString { get; set; }

    /// <summary>
    /// Total number of subtitle lines in the source file. Set when translation starts.
    /// Used to calculate progress from TranslationRequestLines count.
    /// </summary>
    [Column("total_lines")]
    public int TotalLines { get; set; }

    [NotMapped]
    public int[] FailedPositions => string.IsNullOrEmpty(FailedPositionsString)
        ? []
        : FailedPositionsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToArray();

    /// <summary>
    /// Progress is calculated from persisted line count vs total lines.
    /// Returns 100 for completed, 0 for pending (no lines yet).
    /// Actual percentage is computed server-side in GetTranslationRequests.
    /// </summary>
    [NotMapped]
    public int Progress { get; set; }
}
