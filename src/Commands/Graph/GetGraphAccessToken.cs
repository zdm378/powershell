﻿
using System.Management.Automation;

namespace PnP.PowerShell.Commands.Base
{
    [Cmdlet(VerbsCommon.Get, "PnPGraphAccessToken")]
    public class GetGraphAccessToken : PnPGraphCmdlet
    {
        [Parameter(Mandatory = false)]
        public SwitchParameter Decoded;

        protected override void ExecuteCmdlet()
        {
            if (Decoded.IsPresent)
            {
                WriteObject(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(AccessToken));
            }
            else
            {
                WriteObject(AccessToken);
            }
        }
    }
}