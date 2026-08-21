# Estimates of Monitoring Costs (WIP)

## Appendix
### Dynamics

#### Dynamics Conversation Costs

Reference: https://learn.microsoft.com/en-us/dynamics365/customer-service/administer/configure-conversation-diagnostics

The conversation diagnostics data is stored in Azure Application Insights database. Azure Application Insights is an extension of Azure Monitor and charges for data ingested. The two log ingestion plans are Basic and Analytic logs. Learn more about the pricing for your business requirements in [Azure Monitor pricing](https://azure.microsoft.com/pricing/details/monitor/#pricing).

The following table lists the analysis of the average data consumption in Application Insights

| Data Consumption | Size in kilobytes (KB) (average^**1**^) |
| --- | --- |
| Per routed work item (Call/Conversation/Record) with one classification, one route-to-queue ruleset, and one assignment ruleset | 7 |
| Per ruleset with a single rule in it | 2 |
| Per new rule in a ruleset | 1 |

^**1**^ The average values can vary based on factors, such as the number of rules, conditions defined within a ruleset, and size of the conditions (number of characters).

Let's take an example in which each routing stage has a single ruleset with a couple of rules and moderately complex rule conditions. If you route 500 work items per day, it consumes approximately 4.88 MB of data. A breakup is as follows:

7 KB for one work item routed with one ruleset each for classification, route-to-queue, and assignment plus 3 KB for one extra rule at each of the classification, route-to-queue, and assignment rulesets that equals to 10 KB.

10 KB x 500 work items = 5000 KB, which translates to 4.88 MB.

#### Contact Center Health

Reference: [Diagnose contact center health using Application Insights dashboard](https://learn.microsoft.com/en-us/dynamics365/contact-center/use/diagnose-dashboard)
