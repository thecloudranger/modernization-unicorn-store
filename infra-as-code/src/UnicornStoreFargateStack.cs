using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.RDS;
using System.Collections.Generic;
using SecMan = Amazon.CDK.AWS.SecretsManager;
using InfraAsCode.Reusable;

namespace InfraAsCode
{
    public class UnicornStoreFargateStack : Stack
    {
        public UnicornStoreFargateStack(Construct parent, string id, UnicornStoreFargateStackProps settings) : base(parent, id, settings)
        {
            var vpc = new Vpc(this, $"{settings.ScopeName}VPC", new VpcProps { MaxAzs = settings.MaxAzs });

            SecMan.SecretProps databasePasswordSecretDef = SdkExtensions.CreateAutoGenPasswordSecretDef($"{settings.ScopeName}DatabasePassword", passwordLength: 8);
            SecMan.Secret databasePasswordSecret = databasePasswordSecretDef.CreateSecretConstruct(this);

            var database = new DatabaseInstance(this, $"{settings.ScopeName}Database",
                new DatabaseInstanceProps
                {
                    Engine = DatabaseInstanceEngine.SQL_SERVER_EX,
                    InstanceClass = InstanceType.Of(settings.DatabaseInstanceClass, settings.DatabaseInstanceSize),
                    MasterUsername = settings.DbUsername,
                    Vpc = vpc,
                    InstanceIdentifier = $"{settings.ScopeName}Database",
                    DeletionProtection = settings.DotNetEnvironment != "Development",
                    //DatabaseName = $"{stackProps.ScopeName}", // Can't be specified, at least not for SQL Server
                    MasterUserPassword = databasePasswordSecret.SecretValue
                }
            );

            var ecsCluster = new Cluster(this, $"{settings.ScopeName}{settings.Infrastructure}Cluster",
                new ClusterProps {
                    Vpc = vpc,
                    ClusterName = $"{settings.ScopeName}{settings.Infrastructure}Cluster",
                }
            );

            // TODO: replace existing ECR with one created by the Stack
            var imageRepository = Repository.FromRepositoryName(this, "ExistingEcrRepository", settings.DockerImageRepository);

            var secService = new ApplicationLoadBalancedFargateService(this, $"{settings.ScopeName}FargateService",
                new ApplicationLoadBalancedFargateServiceProps
                {
                    Cluster = ecsCluster,
                    DesiredCount = settings.DesiredReplicaCount,
                    Cpu = settings.CpuMillicores,
                    MemoryLimitMiB = settings.MemoryMiB,
                    Image = ContainerImage.FromEcrRepository(imageRepository, settings.ImageTag),
                    PublicLoadBalancer = settings.PublicLoadBalancer,
                    Environment = new Dictionary<string, string>()
                    {
                        { "ASPNETCORE_ENVIRONMENT", settings.DotNetEnvironment ?? "Production" },
                        { "DefaultAdminUsername", settings.DefaultSiteAdminUsername },
                        { "UnicornDbConnectionStringBuilder__DataSource", database.DbInstanceEndpointAddress }, // <- TODO: SQL Server specific, needs to be made parameter-driven
                        { "UnicornDbConnectionStringBuilder__UserId", settings.DbUsername }, // <- TODO: SQL Server specific, needs to be made parameter-driven
                    },
                    Secrets = new Dictionary<string, Secret>
                    {
                        { "DefaultAdminPassword", SdkExtensions.CreateAutoGenPasswordSecretDef($"{settings.ScopeName}DefaultSiteAdminPassword").CreateSecret(this) }, 
                        { "UnicornDbConnectionStringBuilder__Password", databasePasswordSecret.CreateSecret(this, databasePasswordSecretDef.SecretName) }
                    }
                }
            );

            // Update RDS Security Group to allow inbound database connections from the Fargate Service Security Group
            database.Connections.AllowDefaultPortFrom(secService.Service.Connections.SecurityGroups[0]);
        }
    }
}
