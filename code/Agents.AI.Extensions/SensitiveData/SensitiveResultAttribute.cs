namespace Agents.AI.Extensions.SensitiveData;

[AttributeUsage(AttributeTargets.Method)]
public class SensitiveResultAttribute : Attribute
{
    public bool ReturnReference { get; set; } = true;
}
