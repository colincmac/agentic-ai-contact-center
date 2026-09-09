var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.ContactCenter_AIAgent>("contactcenter-aiagent");

builder.Build().Run();
