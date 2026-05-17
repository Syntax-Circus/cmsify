using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cmsify.Infrastructure.Persistence.Interceptors;

public sealed class AuditInterceptor : SaveChangesInterceptor
{
}
