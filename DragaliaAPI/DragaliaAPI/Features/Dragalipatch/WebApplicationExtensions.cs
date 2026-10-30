using DragaliaAPI.Features.GuestAuth;
using DragaliaAPI.Features.Shared.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DragaliaAPI.Features.Dragalipatch;

public static class WebApplicationExtensions
{
    public static WebApplication MapDragalipatchConfigEndpoint(this WebApplication app)
    {
        app.MapGet(
            "/dragalipatch/config",
            (
                [FromServices] IOptionsMonitor<DragalipatchOptions> patchOptions,
                [FromServices] IOptions<GuestAuthOptions> guest,
                HttpRequest request
            ) =>
                new DragalipatchResponse(patchOptions.CurrentValue)
                {
                    UseUnifiedLogin =
                        patchOptions.CurrentValue.UseUnifiedLogin
                        || !guest.Value.Enabled
                        || !guest.Value.ClientEnabled
                        || !request.IsHttps,
                }
        );

        return app;
    }
}
