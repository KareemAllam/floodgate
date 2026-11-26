# Floodgate

A free, open-source Windows app that shows which programs use the network and lets the user limit, prioritize or block them.

## Language

**Filter**:
Describes which traffic something applies to: a program, a domain, an address range, or a combination of these.
_Avoid_: policy, profile

**Rule**:
An action attached to a Filter. A Rule is either a Limit, a Priority, or a Blocker rule.
_Avoid_: policy

**Limit**:
A Rule that caps the speed of the traffic its Filter matches, separately for download and upload.

**Priority**:
A Rule that ranks traffic so that higher-ranked traffic gets bandwidth first.

**Blocker rule**:
A Rule that allows, denies or asks the user about the connections its Filter matches.

**Application**:
All processes started from the same program file. A Limit on an Application is shared by all of its processes.

**Machine**:
The whole computer. A Limit on the Machine applies to traffic of every Application that has no Limit of its own.

**Master switch**:
One switch that turns all enforcement off (Limits, Priorities and Blocker rules). Monitoring continues.

**Ignore data** (tag):
Traffic carrying it is not counted in statistics, quotas or priorities.

**Ignore limit** (tag):
Traffic carrying it is exempt from Limits only.

## Relationships

- A Filter can have many Rules; a Rule belongs to exactly one Filter.
- The most specific Limit wins: Application plus domain or address, then Application, then Machine. A more specific Limit may be higher than a less specific one.
- Among Blocker rules of equal specificity, Deny beats Ask beats Allow; if still tied, the newest Rule wins.
- Ignore tags are applied first, before Blocker rules; Limits and Priorities then apply independently.
- A new Deny rule also closes connections that are already open.
