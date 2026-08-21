using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AI.TestingFramework.AspNetCore;

internal sealed class FakeCertificateHttpClientHandler : HttpClientHandler
{
    public FakeCertificateHttpClientHandler(X509Certificate2 certificate)
    {
        ServerCertificateCustomValidationCallback = (_, serverCertificate, _, errors) =>
        {
            if (serverCertificate is null || !serverCertificate.Equals(certificate))
            {
                return errors == SslPolicyErrors.None;
            }

            return true;
        };
    }
}
