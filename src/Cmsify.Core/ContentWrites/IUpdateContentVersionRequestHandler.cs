using SyntaxCircus.Common;
namespace Cmsify.Core.ContentWrites;
public interface IUpdateContentVersionRequestHandler
{
    Task<Result<UpdatedContentVersionOutput>> HandleAsync(UpdateContentVersionRequest request, CancellationToken cancellationToken);
}
