using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Builder;

namespace AI.TestingFramework.AspNetCore;

internal sealed class FakeStartup
{
    public void Configure(IApplicationBuilder _)
    {
        // intentionally empty
    }

    public void ConfigureServices(IServiceCollection _)
    {
        // intentionally empty
    }
}
