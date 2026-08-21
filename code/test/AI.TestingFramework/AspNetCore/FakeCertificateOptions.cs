using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AI.TestingFramework.AspNetCore;

internal sealed class FakeCertificateOptions
{
    public X509Certificate2? Certificate { get; set; }
}
