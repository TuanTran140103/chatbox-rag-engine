using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Application.Interfaces.Repository;

namespace MarkdownGenQAs.Application.Service;

public class NotificationService
{
    private readonly IProcessBroadcaster _broadcaster;
    private readonly IUnitOfWork _uow;

    public NotificationService(IProcessBroadcaster broadcaster, IUnitOfWork uow)
    {
        _broadcaster = broadcaster;
        _uow = uow;
    }

    public IAsyncEnumerable<NotificationMessage> SubscribeAsync(string processType, Guid documentId, CancellationToken ct)
    {
        return _broadcaster.SubscribeAsync(processType, documentId, ct);
    }

    public IAsyncEnumerable<NotificationMessage> SubscribeWithResumeAsync(string processType, Guid documentId, string? afterId, CancellationToken ct)
    {
        return _broadcaster.SubscribeWithResumeAsync(processType, documentId, afterId, ct);
    }

    public async Task<ServiceResult<IEnumerable<NotificationMessage>>> GetHistoryAsync(Guid documentId, string type)
    {
        try
        {
            var logMessage = await _uow.LogMessages.GetByDocumentIdAsync(documentId);
            if (logMessage == null)
            {
                return new ServiceResult<IEnumerable<NotificationMessage>> { IsSuccess = true, Data = new List<NotificationMessage>() };
            }

            var events = type.ToLower() switch
            {
                "ocr" => logMessage.LogsOcr,
                "gen-qa" => logMessage.LogsGenQa,
                _ => null
            };

            if (events == null)
            {
                return new ServiceResult<IEnumerable<NotificationMessage>> { IsSuccess = false, ErrorMessage = "Invalid process type or no logs available" };
            }

            var notifications = events.Select(e => new NotificationMessage
            {
                DocumentId = documentId,
                Message = e.Message,
                Status = e.Status,
                ProcessingTime = e.ProcessingTime,
                ProcessType = type,
                Timestamp = e.Time
            });

            return new ServiceResult<IEnumerable<NotificationMessage>> { IsSuccess = true, Data = notifications };
        }
        catch (Exception)
        {
            return new ServiceResult<IEnumerable<NotificationMessage>> { IsSuccess = false, ErrorMessage = "Internal server error while fetching logs" };
        }
    }
}
