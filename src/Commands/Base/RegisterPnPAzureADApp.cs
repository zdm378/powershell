﻿using PnP.Framework;
using PnP.Framework.Utilities;

using PnP.PowerShell.Commands.Model;
using PnP.PowerShell.Commands.Utilities;
using PnP.PowerShell.Commands.Utilities.REST;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OperatingSystem = PnP.PowerShell.Commands.Utilities.OperatingSystem;
using Resources = PnP.PowerShell.Commands.Properties.Resources;
using PnP.PowerShell.Commands.Attributes;

namespace PnP.PowerShell.Commands.Base
{
    [Cmdlet(VerbsLifecycle.Register, "PnPAzureADApp")]
    [Alias("Initialize-PnPPowerShellAuthentication")]
    [WriteAliasWarning("Please use 'Register-PnPAzureADApp'. The alias 'Initialize-PnPPowerShellAuthentication' will be removed in the 1.5.0 release")]
    public class InitializePowerShellAuthentication : BasePSCmdlet, IDynamicParameters
    {
        private const string ParameterSet_EXISTINGCERT = "Existing Certificate";
        private const string ParameterSet_NEWCERT = "Generate Certificate";

        private const string ParameterSet_UserName = "By UserName / Password";
        private const string ParameterSet_DeviceLogin = "By Device Login";

        private CancellationTokenSource cancellationTokenSource;


        [Parameter(Mandatory = true, ParameterSetName = ParameterAttribute.AllParameterSets)]
        public string ApplicationName;

        [Parameter(Mandatory = true, ParameterSetName = ParameterAttribute.AllParameterSets)]
        public string Tenant;

        [Parameter(Mandatory = true, ParameterSetName = ParameterSet_EXISTINGCERT)]
        public string CertificatePath;

        [Parameter(Mandatory = false, Position = 0, ParameterSetName = ParameterSet_NEWCERT)]
        public string CommonName;

        [Parameter(Mandatory = false, Position = 1, ParameterSetName = ParameterSet_NEWCERT)]
        public string Country = String.Empty;

        [Parameter(Mandatory = false, Position = 2, ParameterSetName = ParameterSet_NEWCERT)]
        public string State = string.Empty;

        [Parameter(Mandatory = false, Position = 3, ParameterSetName = ParameterSet_NEWCERT)]
        public string Locality = string.Empty;

        [Parameter(Mandatory = false, Position = 4, ParameterSetName = ParameterSet_NEWCERT)]
        public string Organization = string.Empty;

        [Parameter(Mandatory = false, Position = 5, ParameterSetName = ParameterSet_NEWCERT)]
        public string OrganizationUnit = string.Empty;

        [Parameter(Mandatory = false, Position = 7, ParameterSetName = ParameterSet_NEWCERT)]
        public int ValidYears = 10;

        [Parameter(Mandatory = false, Position = 8, ParameterSetName = ParameterSet_NEWCERT)]
        [Parameter(Mandatory = false, Position = 8, ParameterSetName = ParameterSet_EXISTINGCERT)]
        public SecureString CertificatePassword;

        [Parameter(Mandatory = false, ParameterSetName = ParameterSet_NEWCERT)]
        public string OutPath;

        [Parameter(Mandatory = false, ParameterSetName = ParameterSet_NEWCERT)]
        public StoreLocation Store;

        [Parameter(Mandatory = false)]
        public AzureEnvironment AzureEnvironment = AzureEnvironment.Production;

        [Parameter(Mandatory = false)]
        public string Username;

        [Parameter(Mandatory = false)]
        public SecureString Password;

        [Parameter(Mandatory = false)]
        public SwitchParameter DeviceLogin;

        protected override void ProcessRecord()
        {
            var messageWriter = new CmdletMessageWriter(this);
            cancellationTokenSource = new CancellationTokenSource();
            CancellationToken cancellationToken = cancellationTokenSource.Token;

            WriteVerbose(ParameterSetName);

            var loginEndPoint = string.Empty;
            var record = new PSObject();
            using (var authenticationManager = new AuthenticationManager())
            {
                loginEndPoint = authenticationManager.GetAzureADLoginEndPoint(AzureEnvironment);
            }

            string token = string.Empty;
            if (DeviceLogin.IsPresent)
            {
                Task.Factory.StartNew(() =>
                {
                    token = AzureAuthHelper.AuthenticateDeviceLogin(Tenant, ref cancellationToken, messageWriter, loginEndPoint);
                    messageWriter.Stop();
                });
                messageWriter.Start();
            }
            else
            {
                if (PnPConnection.CurrentConnection?.PSCredential != null)
                {
                    Username = PnPConnection.CurrentConnection.PSCredential.UserName;
                    Password = PnPConnection.CurrentConnection.PSCredential.Password;
                }
                if (string.IsNullOrEmpty(Username))
                {
                    throw new PSArgumentException("Username is required or use -DeviceLogin");
                }
                if (Password == null || Password.Length == 0)
                {
                    throw new PSArgumentException("Password is required or use -DeviceLogin");
                }
                token = AzureAuthHelper.AuthenticateAsync(Tenant, Username, Password, loginEndPoint).GetAwaiter().GetResult();
            }

            var cert = new X509Certificate2();
            if (ParameterSetName == ParameterSet_EXISTINGCERT)
            {
                // Ensure a file exists at the provided CertificatePath
                if (!File.Exists(CertificatePath))
                {
                    throw new PSArgumentException(string.Format(Resources.CertificateNotFoundAtPath, CertificatePath), nameof(CertificatePath));
                }

                if (ParameterSpecified(nameof(CertificatePassword)))
                {
                    try
                    {
                        cert.Import(CertificatePath, CertificatePassword, X509KeyStorageFlags.Exportable);
                    }
                    catch (CryptographicException e) when (e.Message.Contains("The specified network password is not correct"))
                    {
                        throw new PSArgumentNullException(nameof(CertificatePassword), string.Format(Resources.PrivateKeyCertificateImportFailedPasswordIncorrect, nameof(CertificatePassword)));
                    }
                }
                else
                {
                    try
                    {
                        cert.Import(CertificatePath);
                    }
                    catch (CryptographicException e) when (e.Message.Contains("The specified network password is not correct"))
                    {
                        throw new PSArgumentNullException(nameof(CertificatePassword), string.Format(Resources.PrivateKeyCertificateImportFailedPasswordMissing, nameof(CertificatePassword)));
                    }
                }

                // Ensure the certificate at the provided CertificatePath holds a private key
                if (!cert.HasPrivateKey)
                {
                    throw new PSArgumentException(string.Format(Resources.CertificateAtPathHasNoPrivateKey, CertificatePath), nameof(CertificatePath));
                }
            }
            else
            {
#if NETFRAMEWORK
                    // Generate a certificate
                    var x500Values = new List<string>();
                    if (!MyInvocation.BoundParameters.ContainsKey("CommonName"))
                    {
                        CommonName = ApplicationName;
                    }
                    if (!string.IsNullOrWhiteSpace(CommonName)) x500Values.Add($"CN={CommonName}");
                    if (!string.IsNullOrWhiteSpace(Country)) x500Values.Add($"C={Country}");
                    if (!string.IsNullOrWhiteSpace(State)) x500Values.Add($"S={State}");
                    if (!string.IsNullOrWhiteSpace(Locality)) x500Values.Add($"L={Locality}");
                    if (!string.IsNullOrWhiteSpace(Organization)) x500Values.Add($"O={Organization}");
                    if (!string.IsNullOrWhiteSpace(OrganizationUnit)) x500Values.Add($"OU={OrganizationUnit}");

                    string x500 = string.Join("; ", x500Values);

                    if (ValidYears < 1 || ValidYears > 30)
                    {
                        ValidYears = 10;
                    }
                    DateTime validFrom = DateTime.Today;
                    DateTime validTo = validFrom.AddYears(ValidYears);

                    byte[] certificateBytes = CertificateHelper.CreateSelfSignCertificatePfx(x500, validFrom, validTo, CertificatePassword);
                    cert = new X509Certificate2(certificateBytes, CertificatePassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
#else
                DateTimeOffset validFrom = DateTimeOffset.Now;
                DateTimeOffset validTo = validFrom.AddYears(ValidYears);
                cert = CertificateHelper.CreateSelfSignedCertificate2(CommonName, Country, State, Locality, Organization, OrganizationUnit, 2048, null, null, validFrom, validTo, "", false, null);
#endif

                if (!string.IsNullOrWhiteSpace(OutPath))
                {
                    if (!Path.IsPathRooted(OutPath))
                    {
                        OutPath = Path.Combine(SessionState.Path.CurrentFileSystemLocation.Path, OutPath);
                    }
                    if (Directory.Exists(OutPath))
                    {
                        var pfxPath = Path.Combine(OutPath, $"{ApplicationName}.pfx");
                        byte[] certPfxData = cert.Export(X509ContentType.Pfx, CertificatePassword);
                        File.WriteAllBytes(pfxPath, certPfxData);
                        record.Properties.Add(new PSVariableProperty(new PSVariable("Pfx file", pfxPath)));
                        var cerPath = Path.Combine(OutPath, $"{ApplicationName}.cer");
                        byte[] certCerData = cert.Export(X509ContentType.Cert);
                        File.WriteAllBytes(cerPath, certCerData);
                        record.Properties.Add(new PSVariableProperty(new PSVariable("Cer file", cerPath)));
                    }
                }
                if (ParameterSpecified(nameof(Store)))
                {
                    using (var store = new X509Store("My", Store))
                    {
                        store.Open(OpenFlags.ReadWrite);
                        store.Add(cert);
                        store.Close();
                    }
                    Host.UI.WriteLine(ConsoleColor.Yellow, Host.UI.RawUI.BackgroundColor, "Certificate added to store");
                }
            }

            var expirationDate = DateTime.Parse(cert.GetExpirationDateString()).ToUniversalTime();
            var startDate = DateTime.Parse(cert.GetEffectiveDateString()).ToUniversalTime();

            if (token != null)
            {
                var permissionScopes = new PermissionScopes();
                var scopes = new List<PermissionScope>();
                if (this.Scopes != null)
                {
                    foreach (var scopeIdentifier in this.Scopes)
                    {
                        scopes.Add(permissionScopes.GetScope(scopeIdentifier));
                    }
                }
                else
                {
                    scopes.Add(permissionScopes.GetScope("SPO.Sites.FullControl.All"));
                    scopes.Add(permissionScopes.GetScope("MSGraph.Group.ReadWrite.All"));
                    scopes.Add(permissionScopes.GetScope("SPO.User.Read.All"));
                    scopes.Add(permissionScopes.GetScope("MSGraph.User.Read.All"));
                }

                var scopesPayload = GetScopesPayload(scopes);
                var payload = new
                {
                    displayName = ApplicationName,
                    signInAudience = "AzureADMyOrg",
                    keyCredentials = new[] {
                        new {
                            customKeyIdentifier = cert.GetCertHashString(),
                            endDateTime = expirationDate,
                            keyId = Guid.NewGuid().ToString(),
                            startDateTime = startDate,
                            type= "AsymmetricX509Cert",
                            usage= "Verify",
                            key = Convert.ToBase64String(cert.GetRawCertData())
                        }
                    },
                    publicClient = new
                    {
                        redirectUris = new[] {
                            $"{loginEndPoint}/common/oauth2/nativeclient"
                        }
                    },
                    requiredResourceAccess = scopesPayload
                };
                var requestContent = new StringContent(JsonSerializer.Serialize(payload));
                requestContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                var azureApp = GraphHelper.PostAsync<AzureApp>(new System.Net.Http.HttpClient(), "/beta/applications", requestContent, token).GetAwaiter().GetResult();
                record.Properties.Add(new PSVariableProperty(new PSVariable("AzureAppId", azureApp.AppId)));

                var consentUrl = $"{loginEndPoint}/{Tenant}/v2.0/adminconsent?client_id={azureApp.AppId}&scope=https://microsoft.sharepoint-df.com/.default";
                record.Properties.Add(new PSVariableProperty(new PSVariable("Certificate Thumbprint", cert.GetCertHashString())));


                if (OperatingSystem.IsWindows())
                {
                    var waitTime = 60;
                    Host.UI.WriteLine(ConsoleColor.Yellow, Host.UI.RawUI.BackgroundColor, $"Waiting {waitTime} seconds to launch consent flow in a browser window. This wait is required to make sure that Azure AD is able to initialize all required artifacts. After you provided consent you will see a blank page. This is expected. You can always navigate to the consent page manually: {consentUrl}");

                    for (var i = 0; i < waitTime; i++)
                    {
                        Host.UI.Write(ConsoleColor.Yellow, Host.UI.RawUI.BackgroundColor, ".");
                        System.Threading.Thread.Sleep(1000);

                        // Check if CTRL+C has been pressed and if so, abort the wait
                        if (Stopping)
                        {
                            break;
                        }
                    }
                    Host.UI.WriteLine();


                    WriteObject(record);

                    BrowserHelper.LaunchBrowser(consentUrl);
                }
                else
                {
                    Host.UI.WriteLine(ConsoleColor.Yellow, Host.UI.RawUI.BackgroundColor, $"Please wait approximately 60 seconds, then open the following URL in a browser window to provide consent. This consent is required in order to use this application. After you provided consent you will see a blank page, this is correct.\n\n{consentUrl}");
                    WriteObject(record);
                }
            }
        }

        protected override void StopProcessing()
        {
            cancellationTokenSource.Cancel();
        }

        private static object GetScopesPayload(List<PermissionScope> scopes)
        {
            var resourcePermissions = new List<AppResource>();
            var distinctResources = scopes.GroupBy(s => s.resourceAppId).Select(r => r.First()).ToList();
            foreach (var distinctResource in distinctResources)
            {
                var id = distinctResource.resourceAppId;
                var appResource = new AppResource() { Id = id };
                appResource.ResourceAccess.AddRange(scopes.Where(s => s.resourceAppId == id).ToList());
                resourcePermissions.Add(appResource);
            }
            return resourcePermissions;
        }

        protected IEnumerable<string> Scopes
        {
            get
            {
                if (ParameterSpecified(nameof(Scopes)) && MyInvocation.BoundParameters["Scopes"] != null)
                {
                    return MyInvocation.BoundParameters["Scopes"] as string[];
                }
                else
                {
                    return null;
                }
            }
        }

        public object GetDynamicParameters()
        {
            var classAttribute = this.GetType().GetCustomAttributes(false).FirstOrDefault(a => a is PropertyLoadingAttribute);
            const string parameterName = "Scopes";

            var parameterDictionary = new RuntimeDefinedParameterDictionary();
            var attributeCollection = new System.Collections.ObjectModel.Collection<Attribute>();

            var parameterAttribute = new ParameterAttribute
            {
                ValueFromPipeline = false,
                ValueFromPipelineByPropertyName = false,
                Mandatory = false
            };

            attributeCollection.Add(parameterAttribute);

            var identifiers = new PermissionScopes().GetIdentifiers();

            var validateSetAttribute = new ValidateSetAttribute(identifiers);
            attributeCollection.Add(validateSetAttribute);

            var runtimeParameter = new RuntimeDefinedParameter(parameterName, typeof(string[]), attributeCollection);

            parameterDictionary.Add(parameterName, runtimeParameter);

            return parameterDictionary;
        }
    }
}