using System.Runtime.CompilerServices;
using MarkdownGenQAs.Application.Interfaces.Services;
using MarkdownGenQAs.Models;
using MarkdownGenQAs.Application.Interfaces.Repository;
using MarkdownGenQAs.Models.Enum;

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

    public async IAsyncEnumerable<NotificationMessage> SubscribeWithResumeAsync(
        string processType,
        Guid documentId,
        string? afterId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var document = await _uow.Documents.GetByIdAsync(documentId);
        if (document == null)
        {
            yield return new NotificationMessage
            {
                DocumentId = documentId,
                Status = "not_found",
                Message = "Document not found",
                ProcessType = processType
            };
            yield break;
        }

        var documentJob = await _uow.DocumentJobs.GetByDocumentIdAsync(documentId);

        switch (processType)
        {
            case "ocr":
                if (document.Status != StatusDocument.ProcessingOcr
                    || (documentJob?.StatusOcr != StatusJob.Processing && documentJob?.StatusOcr != StatusJob.Pending))
                {
                    yield return new NotificationMessage
                    {
                        DocumentId = documentId,
                        Status = document.Status.ToString(),
                        Message = $"OCR is not currently processing. Document status: {document.Status}",
                        ProcessType = processType
                    };
                    yield break;
                }
                break;

            case "indexing":
                if (document.Status != StatusDocument.ProcessingIndexing
                    || (documentJob?.StatusIndexing != StatusJob.Processing && documentJob?.StatusIndexing != StatusJob.Pending))
                {
                    yield return new NotificationMessage
                    {
                        DocumentId = documentId,
                        Status = document.Status.ToString(),
                        Message = $"Indexing is not currently processing. Document status: {document.Status}",
                        ProcessType = processType
                    };
                    yield break;
                }
                break;
        }

        await foreach (var message in _broadcaster.SubscribeWithResumeAsync(processType, documentId, afterId, ct))
        {
            yield return message;
        }
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
                "indexing" => logMessage.LogsIndexing,
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
