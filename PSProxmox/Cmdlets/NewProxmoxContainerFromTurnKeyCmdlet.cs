using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Security;
using Newtonsoft.Json.Linq;
using PSProxmox.Client;
using PSProxmox.Models;
using PSProxmox.Session;
using PSProxmox.Utilities;

namespace PSProxmox.Cmdlets
{
    /// <summary>
    /// <para type="synopsis">Creates a new Proxmox LXC container from a TurnKey Linux template</para>
    /// <para type="description">Creates a new Proxmox LXC container from a TurnKey Linux template on a Proxmox server</para>
    /// </summary>
    /// <example>
    /// <code>New-ProxmoxContainerFromTurnKey -Node "pve1" -Name "wordpress" -Template "wordpress" -Storage "local-lvm" -Memory 512 -Cores 1 -DiskSize 8</code>
    /// <para>Creates a new LXC container from the WordPress TurnKey Linux template on node pve1</para>
    /// </example>
    [Cmdlet(VerbsCommon.New, "ProxmoxContainerFromTurnKey")]
    [OutputType(typeof(ProxmoxContainer))]
    public class NewProxmoxContainerFromTurnKeyCmdlet : ProxmoxCmdlet
    {
        /// <summary>
        /// Gets or sets the node name
        /// </summary>
        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        public string Node { get; set; }

        /// <summary>
        /// Gets or sets the container name
        /// </summary>
        [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the TurnKey template name
        /// </summary>
        [Parameter(Mandatory = true, Position = 2, ValueFromPipelineByPropertyName = true)]
        public string Template { get; set; }

        /// <summary>
        /// Gets or sets the container ID
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public int? CTID { get; set; }

        /// <summary>
        /// Gets or sets the storage name
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public string Storage { get; set; } = "local";

        /// <summary>
        /// Gets or sets the container memory limit in MB
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public int Memory { get; set; } = 512;

        /// <summary>
        /// Gets or sets the container swap limit in MB
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public int Swap { get; set; } = 512;

        /// <summary>
        /// Gets or sets the container CPU cores
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public int Cores { get; set; } = 1;

        /// <summary>
        /// Gets or sets the container disk size in GB
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public int DiskSize { get; set; } = 8;

        /// <summary>
        /// Gets or sets a value indicating whether the container is unprivileged
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public SwitchParameter Unprivileged { get; set; }

        /// <summary>
        /// Gets or sets the container password
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public SecureString Password { get; set; }

        /// <summary>
        /// Gets or sets the container SSH public key
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public string SSHKey { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to start the container on boot
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public SwitchParameter StartOnBoot { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to start the container after creation
        /// </summary>
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true)]
        public SwitchParameter Start { get; set; }

        /// <summary>
        /// Processes the cmdlet
        /// </summary>
        protected override void ProcessRecord()
        {
            base.ProcessRecord();

            try
            {
                // Get the template
                var templatePath = GetTemplatePath(Node, Template, Storage);
                if (string.IsNullOrEmpty(templatePath))
                {
                    // Try to download the template
                    WriteVerbose($"Template '{Template}' not found, attempting to download...");
                    var downloadCmdlet = new SaveProxmoxTurnKeyTemplateCmdlet
                    {
                        Connection = Connection,
                        Node = Node,
                        Name = Template,
                        Storage = Storage
                    };

                    var result = InvokeCommand.InvokeScript(false, ScriptBlock.Create("param($cmdlet) & { $cmdlet.ProcessRecord(); return $cmdlet.CommandRuntime.ToString() }"), null, downloadCmdlet).FirstOrDefault();
                    templatePath = result?.ToString();

                    if (string.IsNullOrEmpty(templatePath))
                    {
                        throw new Exception($"Failed to download template '{Template}'");
                    }
                }

                // Create the container
                var client = GetProxmoxClient();
                var parameters = new Dictionary<string, string>
                {
                    { "hostname", Name },
                    { "ostemplate", templatePath },
                    { "storage", Storage },
                    { "memory", Memory.ToString() },
                    { "swap", Swap.ToString() },
                    { "cores", Cores.ToString() },
                    { "rootfs", $"{Storage}:{DiskSize}" }
                };

                if (CTID.HasValue)
                {
                    parameters["vmid"] = CTID.Value.ToString();
                }

                if (Unprivileged.IsPresent)
                {
                    parameters["unprivileged"] = "1";
                }

                if (Password != null)
                {
                    parameters["password"] = ConvertSecureStringToString(Password);
                }

                if (!string.IsNullOrEmpty(SSHKey))
                {
                    parameters["ssh-public-keys"] = SSHKey;
                }

                if (StartOnBoot.IsPresent)
                {
                    parameters["onboot"] = "1";
                }

                if (Start.IsPresent)
                {
                    parameters["start"] = "1";
                }

                var response = client.Post($"nodes/{Node}/lxc", parameters);
                var responseData = JsonUtility.DeserializeResponse<JObject>(response);
                var ctid = int.Parse(responseData["vmid"]?.ToString() ?? "0");

                // Wait for the task to complete
                var taskId = responseData["upid"]?.ToString();
                var taskStatus = WaitForTask(Node, taskId);

                if (taskStatus != "OK")
                {
                    throw new Exception($"Failed to create container: {taskStatus}");
                }

                // Get the created container
                var container = GetContainer(Node, ctid);
                WriteObject(container);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "NewProxmoxContainerFromTurnKeyError", ErrorCategory.OperationStopped, null));
            }
        }

        private string GetTemplatePath(string node, string templateName, string storage)
        {
            try
            {
                var client = GetProxmoxClient();
                var response = client.Get($"nodes/{node}/storage/{storage}/content");
                var data = JsonUtility.DeserializeResponse<JArray>(response);

                foreach (var item in data)
                {
                    if (item["content"]?.ToString() == "vztmpl" &&
                        item["volid"]?.ToString()?.Contains(templateName) == true)
                    {
                        return item["volid"]?.ToString();
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors and return null
            }

            return null;
        }

        private string WaitForTask(string node, string taskId)
        {
            var client = GetProxmoxClient();
            var status = "";
            var attempts = 0;
            var maxAttempts = 60; // Wait up to 60 seconds

            while (attempts < maxAttempts)
            {
                var response = client.Get($"nodes/{node}/tasks/{taskId}/status");
                var data = JsonUtility.DeserializeResponse<JObject>(response);
                status = data["status"]?.ToString();

                if (status == "stopped")
                {
                    return data["exitstatus"]?.ToString() ?? "OK";
                }

                System.Threading.Thread.Sleep(1000);
                attempts++;
            }

            throw new Exception("Timeout waiting for task to complete");
        }

        private ProxmoxContainer GetContainer(string node, int ctid)
        {
            var client = GetProxmoxClient();
            var response = client.Get($"nodes/{node}/lxc/{ctid}/status/current");
            var container = JsonUtility.DeserializeResponse<ProxmoxContainer>(response);

            container.Node = node;
            container.CTID = ctid;

            return container;
        }

        private string ConvertSecureStringToString(SecureString secureString)
        {
            var ptr = System.Runtime.InteropServices.Marshal.SecureStringToBSTR(secureString);
            try
            {
                return System.Runtime.InteropServices.Marshal.PtrToStringBSTR(ptr);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ZeroFreeBSTR(ptr);
            }
        }
    }
}
