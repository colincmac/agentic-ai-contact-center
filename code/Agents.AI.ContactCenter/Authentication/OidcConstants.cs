using System;
using System.Collections.Generic;
using System.Text;

namespace Agents.AI.ContactCenter.Authentication;

public static class OidcConstants
{
    public static class AuthenticationMethods
    {
        public const string Geolocation = "geo";
        public const string ProofOfPossessionHardwareSecuredKey = "hwk";
        public const string KnowledgeBasedAuthentication = "kba";
        public const string MultipleChannelAuthentication = "mca";
        public const string MultiFactorAuthentication = "mfa";
        public const string OneTimePassword = "otp";
        public const string PersonalIdentificationOrPattern = "pin";
        public const string ProofOfPossessionKey = "pop";
        public const string RiskBasedAuthentication = "rba";
        public const string ConfirmationBySms = "sms";
        public const string ProofOfPossessionSoftwareSecuredKey = "swk";
        public const string ConfirmationByTelephone = "tel";
        public const string UserPresenceTest = "user";
        public const string VoiceBiometric = "vbm";
    }
}
