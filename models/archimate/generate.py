# Makes the CSV files of the ArchiMate example: the hand-written core in core/ (a small retail group) plus a generated
# enterprise around it, as a large model to try the app on. Everything is invented, and a fixed seed makes the output the
# same every time. Run: python generate.py [folder to write to, this one if left out]
#
# The group has 25 markets with 4 business lines each. Every one of those 100 units gets its own business, application,
# technology, strategy and motivation elements, linked inside the unit and out to the group's shared ones (the core plus
# some generated ones: shared platforms, group goals, principles and values). That is what makes the links add up.
import csv, os, random, sys, datetime as dt

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = sys.argv[1] if len(sys.argv) > 1 else HERE
CORE = os.path.join(HERE, "core")
R = random.Random(20261115)

# type -> (file, columns). The first two columns are always the key and the name.
TYPES = {
    "BusinessActor": ("actors.csv", ["id", "name", "actor_type", "department"]),
    "BusinessRole": ("roles.csv", ["id", "name", "description"]),
    "BusinessProcess": ("processes.csv", ["id", "name", "frequency", "owner_department"]),
    "BusinessService": ("business_services.csv", ["id", "name", "channel"]),
    "BusinessObject": ("business_objects.csv", ["id", "name", "retention_period"]),
    "ApplicationComponent": ("app_components.csv", ["id", "name", "vendor", "lifecycle_status"]),
    "ApplicationService": ("app_services.csv", ["id", "name", "availability"]),
    "DataObject": ("data_objects.csv", ["id", "name", "classification"]),
    "TechnologyNode": ("tech_nodes.csv", ["id", "name", "environment", "hosting"]),
    "TechnologyService": ("tech_services.csv", ["id", "name", "provider"]),
    "Resource": ("resources.csv", ["id", "name", "resource_type"]),
    "Capability": ("capabilities.csv", ["id", "name", "maturity", "importance"]),
    "ValueStream": ("value_streams.csv", ["id", "name", "customer_facing"]),
    "CourseOfAction": ("courses_of_action.csv", ["id", "name", "status", "horizon"]),
    "Stakeholder": ("stakeholders.csv", ["id", "name", "stakeholder_type", "influence"]),
    "Driver": ("drivers.csv", ["id", "name", "driver_type"]),
    "Assessment": ("assessments.csv", ["id", "name", "rating"]),
    "Goal": ("goals.csv", ["id", "name", "priority"]),
    "Outcome": ("outcomes.csv", ["id", "name", "target_date"]),
    "Principle": ("principles.csv", ["id", "name", "rationale"]),
    "Requirement": ("requirements.csv", ["id", "name", "kind", "priority"]),
    "Constraint": ("constraints.csv", ["id", "name", "constraint_type"]),
    "Meaning": ("meanings.csv", ["id", "name"]),
    "Value": ("values.csv", ["id", "name", "value_type"]),
}
ABBR = dict(BusinessActor="act", BusinessRole="rol", BusinessProcess="prc", BusinessService="bsv", BusinessObject="bob",
            ApplicationComponent="cmp", ApplicationService="asv", DataObject="dat", TechnologyNode="nod",
            TechnologyService="tsv", Resource="res", Capability="cap", ValueStream="vst", CourseOfAction="coa",
            Stakeholder="stk", Driver="drv", Assessment="ass", Goal="gol", Outcome="out", Principle="pri",
            Requirement="req", Constraint="con", Meaning="mea", Value="val")

# file -> columns, for the relationships. Extra columns after the two ends are the link's own values.
RELS = {
    "actor_roles.csv": ["actor", "role"],
    "role_processes.csv": ["role", "process"],
    "process_realizes_service.csv": ["process", "service"],
    "process_triggers.csv": ["from", "to"],
    "process_accesses_object.csv": ["process", "object", "access_type"],
    "app_service_serves_process.csv": ["service", "process"],
    "component_realizes_service.csv": ["component", "service"],
    "component_accesses_data.csv": ["component", "data", "access_type"],
    "data_realizes_object.csv": ["data", "object"],
    "component_deployed_on_node.csv": ["component", "node"],
    "techservice_serves_component.csv": ["service", "component"],
    "node_realizes_techservice.csv": ["node", "service"],
    "component_flows_to_component.csv": ["from", "to", "frequency"],
    "business_service_serves_actor.csv": ["service", "actor"],
    "capability_composition.csv": ["parent", "child"],
    "process_realizes_capability.csv": ["process", "capability"],
    "component_realizes_resource.csv": ["component", "resource"],
    "node_realizes_resource.csv": ["node", "resource"],
    "resource_realizes_capability.csv": ["resource", "capability"],
    "capability_serves_value_stream.csv": ["capability", "stream"],
    "value_stream_serves_actor.csv": ["stream", "actor"],
    "course_aggregates_capability.csv": ["course", "capability"],
    "course_realizes_goal.csv": ["course", "goal"],
    "capability_realizes_requirement.csv": ["capability", "requirement"],
    "stakeholder_driver.csv": ["stakeholder", "driver"],
    "actor_stakeholder.csv": ["actor", "stakeholder"],
    "driver_influences_driver.csv": ["from", "to", "strength"],
    "driver_assessment.csv": ["driver", "assessment"],
    "assessment_influences_goal.csv": ["assessment", "goal", "strength"],
    "goal_aggregation.csv": ["parent", "child"],
    "outcome_realizes_goal.csv": ["outcome", "goal"],
    "principle_realizes_goal.csv": ["principle", "goal"],
    "requirement_realizes_outcome.csv": ["requirement", "outcome"],
    "requirement_realizes_principle.csv": ["requirement", "principle"],
    "constraint_influences_requirement.csv": ["constraint", "requirement", "strength"],
    "process_realizes_requirement.csv": ["process", "requirement"],
    "business_service_realizes_requirement.csv": ["service", "requirement"],
    "app_service_realizes_requirement.csv": ["service", "requirement"],
    "component_realizes_requirement.csv": ["component", "requirement"],
    "node_realizes_requirement.csv": ["node", "requirement"],
    "meaning_object.csv": ["meaning", "object"],
    "business_service_value.csv": ["service", "value"],
    "stakeholder_value.csv": ["stakeholder", "value"],
}

rows = {t: [] for t in TYPES}
links = {f: [] for f in RELS}
seen = {f: set() for f in RELS}


def read_core(name):
    path = os.path.join(CORE, name)
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


# ---- vocabulary -------------------------------------------------------------------------------------------------------
MARKETS = [("gb", "United Kingdom"), ("ie", "Ireland"), ("fr", "France"), ("de", "Germany"), ("nl", "Netherlands"),
           ("be", "Belgium"), ("es", "Spain"), ("pt", "Portugal"), ("it", "Italy"), ("ch", "Switzerland"), ("at", "Austria"),
           ("pl", "Poland"), ("se", "Sweden"), ("no", "Norway"), ("dk", "Denmark"), ("fi", "Finland"), ("cz", "Czechia"),
           ("gr", "Greece"), ("hu", "Hungary"), ("ro", "Romania"), ("hr", "Croatia"), ("sk", "Slovakia"),
           ("bg", "Bulgaria"), ("lt", "Lithuania"), ("lv", "Latvia")]
LINES = [("st", "Stores"), ("on", "Online"), ("wh", "Wholesale"), ("lg", "Logistics")]
AREAS = ["Order", "Stock", "Payment", "Returns", "Pricing", "Supplier", "Delivery", "Fraud", "Customer", "Promotion"]
DEPARTMENTS = ["Retail Operations", "Logistics", "Finance", "Marketing", "Technology", "Procurement", "Risk and Compliance",
               "Customer Care", "People", "Merchandising"]
TITLES = ["Manager", "Associate", "Analyst", "Coordinator", "Controller", "Planner", "Advisor", "Lead", "Specialist", "Clerk"]
VERBS = ["Capture", "Validate", "Approve", "Schedule", "Reconcile", "Escalate", "Review", "Forecast", "Allocate", "Dispatch",
         "Audit", "Notify", "Archive", "Cancel", "Amend", "Track", "Price", "Replenish", "Inspect", "Onboard"]
OBJECTS = ["Order", "Payment", "Return", "Delivery", "Stock Transfer", "Invoice", "Supplier Contract", "Customer Complaint",
           "Price Change", "Promotion", "Gift Card", "Loyalty Claim", "Store Opening", "Shelf Layout", "Shipment", "Refund",
           "Credit Note", "Fraud Alert", "Product Listing", "Staff Rota"]
ROLE_KINDS = ["Handler", "Controller", "Coordinator", "Reviewer", "Approver", "Planner", "Owner", "Analyst"]
SERVICE_KINDS = ["Online", "In-Store", "Self-Service", "Assisted", "Express", "Subscription"]
OBJECT_KINDS = ["Record", "Form", "Report", "Contract", "Note", "Notice", "Statement"]
PRODUCTS = ["Order Hub", "Stock Sync", "Pay Bridge", "Route Planner", "Price Studio", "Loyalty Core", "Returns Desk",
            "Supplier Gate", "Fraud Watch", "Insight Board", "Catalog Manager", "Rota Planner", "Shelf Planner", "Tax Engine",
            "Label Printer", "Gift Card Service", "Notification Hub", "Search Index", "Basket Service", "Checkout UI"]
PRODUCT_KINDS = ["Core", "Edge", "Mobile", "Web", "Batch", "API", "Sync", "Admin"]
SERVICE_VERBS = ["Lookup", "Validation", "Authorisation", "Submission", "Scheduling", "Calculation", "Notification", "Search",
                 "Export", "Import"]
DATA_KINDS = ["Master", "Transaction", "Snapshot", "Audit Log", "Reference", "Archive", "Extract"]
NODE_KINDS = ["App Server", "DB Server", "Cache Node", "Queue Broker", "Edge Gateway", "Batch Runner", "Storage Array",
              "Load Balancer"]
TECH_SERVICES = ["Compute", "Database", "Messaging", "Storage", "Network", "Identity", "Monitoring", "Backup", "Search",
                 "Secrets"]
CAPABILITY_KINDS = ["Management", "Planning", "Execution", "Analytics", "Compliance"]
RESOURCES = [("Warehouse Space", "physical"), ("Delivery Fleet", "physical"), ("Store Estate", "physical"),
             ("Data Platform", "intangible"), ("Brand", "intangible"), ("Workforce Pool", "human"),
             ("Working Capital", "financial"), ("Supplier Network", "intangible"), ("Loyalty Base", "intangible"),
             ("Licence Portfolio", "intangible"), ("Cloud Capacity", "physical"), ("Contact Centre Staff", "human"),
             ("Capital Budget", "financial"), ("Distribution Hubs", "physical"), ("Engineering Team", "human")]
STREAMS = ["Order to Cash", "Source to Pay", "Return to Refund", "Plan to Stock", "Hire to Retire", "Idea to Launch"]
INITIATIVES = ["Modernise {}", "Consolidate {} Platforms", "Automate {}", "Expand {} Self-Service", "Outsource {}",
               "Retire Legacy {}"]
STAKEHOLDERS = [("Customers", "external"), ("Shareholders", "external"), ("Regulators", "external"),
                ("Employees", "internal"), ("Suppliers", "external"), ("Unions", "external"), ("Board", "internal"),
                ("Local Authority", "external"), ("Franchise Partners", "external"), ("Carriers", "external"),
                ("Investors", "external"), ("Community Groups", "external")]
DRIVERS = [("Rising Energy Costs", "financial"), ("Online Competition", "competitive"), ("Data Protection Rules", "regulatory"),
           ("Shifting Customer Habits", "customer"), ("Supply Chain Volatility", "market"), ("Labour Shortages", "market"),
           ("Payments Regulation", "regulatory"), ("Sustainability Targets", "regulatory"), ("Price Pressure", "competitive"),
           ("Faster Delivery Expectations", "customer"), ("Cyber Threats", "technology"), ("Ageing Platforms", "technology")]
ASSESSMENTS = [("Ageing {} platform", "weakness"), ("Strong {} team", "strength"), ("Growing {} demand", "opportunity"),
               ("Rising {} risk", "threat"), ("Slow {} reporting", "weakness"), ("High {} accuracy", "strength"),
               ("Fragmented {} data", "weakness"), ("Room to automate {}", "opportunity")]
GOALS = ["Reduce {} cost", "Improve {} accuracy", "Speed up {}", "Increase {} satisfaction", "Grow {} revenue", "Cut {} risk"]
OUTCOME_TEMPLATES = ["Cut {} handling time by {}%", "Raise {} accuracy to {}%", "Lower {} cost by {}%",
                     "Reach {}% of {} done without help"]
PRINCIPLES = [("Buy before build", "Bought products cost less to keep than ours."),
              ("Data is a shared asset", "One source of truth saves reconciling."),
              ("Design for failure", "Every dependency will be down some day."),
              ("Prefer standard interfaces", "Standards make parts easy to swap."),
              ("One owner per process", "Shared ownership leaves problems unowned."),
              ("Automate by default", "Manual steps are where errors creep in."),
              ("Secure by design", "Security added later costs more."),
              ("Reuse before new", "A second copy is a second thing to maintain."),
              ("Customer first", "Choices are judged by what customers see."),
              ("Measure what matters", "What isn't measured drifts."),
              ("Cloud where it fits", "Run it where it costs least to run."),
              ("Small changes, often", "Small releases are easy to undo.")]
REQUIREMENTS = ["{} shall be available 24x7", "{} shall keep an audit trail", "{} shall respond within 2 seconds",
                "{} shall support refunds", "{} shall encrypt customer data", "{} shall reconcile daily",
                "{} shall cope with peak load", "{} shall expose an API"]
REQUIREMENT_KINDS = {"available": "non_functional", "audit": "regulatory", "respond": "non_functional", "refunds": "functional",
                     "encrypt": "security", "reconcile": "functional", "peak": "non_functional", "expose": "functional"}
CONSTRAINTS = [("Budget cap on {}", "budget"), ("Freeze window for {}", "time"), ("Data residency for {}", "legal"),
               ("Vendor lock-in on {}", "technical"), ("Legacy dependency in {}", "technical"), ("Contract term on {}", "legal")]
MEANINGS = ["Basket", "Loyalty Tier", "Delivery Window", "Substitution", "Chargeback", "Margin", "Stock-out", "Peak Week"]
VALUES = [("Convenience", "convenience"), ("Trust", "assurance"), ("Low Price", "monetary"), ("Speed", "experience"),
          ("Choice", "experience"), ("Sustainability", "assurance"), ("Reliability", "assurance")]
STRENGTHS = ["strong_negative", "negative", "positive", "strong_positive"]
ACCESS = ["read", "write", "read_write"]
FREQUENCIES = ["realtime", "hourly", "daily", "batch"]


def weighted(options):
    return R.choices([o for o, _ in options], [w for _, w in options])[0]


def names(combos, n):
    """n distinct names from combos, numbered when there aren't enough."""
    picked = R.sample(combos, min(n, len(combos)))
    extra = n - len(picked)
    return picked + [f"{R.choice(combos)} {2 + i // len(combos)}" for i in range(extra)]


def add(kind, key, name, *values):
    rows[kind].append([key, name, *values])
    return key


def link(file, a, b, *values):
    """One link, unless that pair is already linked."""
    if a == b and file not in ("process_triggers.csv", "component_flows_to_component.csv", "driver_influences_driver.csv"):
        return
    if (a, b) in seen[file]:
        return
    seen[file].add((a, b))
    links[file].append([a, b, *values])


def fan(file, sources, targets, lo, hi, shared=None, share=0.0, reverse=False, value=None):
    """Links each of sources to lo..hi of targets, some taken from shared. reverse puts the target first."""
    for s in sources:
        k = R.randint(lo, hi)
        chosen = set()
        for _ in range(k):
            pool = shared if shared and R.random() < share else targets
            if pool:
                chosen.add(R.choice(pool))
        for t in sorted(chosen):
            extra = [value()] if value else []
            if reverse:
                link(file, t, s, *extra)
            else:
                link(file, s, t, *extra)


# ---- one unit ---------------------------------------------------------------------------------------------------------
def build(code, label, grp, scale=1.0):
    """Makes a unit's elements and links, with grp (the group's shared ones) to link out to. Returns its elements."""
    U = {}
    counter = {}

    def make(kind, n, fields):
        """fields(name_or_tuple_index) -> (name, values...). Adds n elements and remembers their keys."""
        keys = []
        for i in range(max(1, round(n * scale))):
            counter[kind] = counter.get(kind, 0) + 1
            key = f"{code}-{ABBR[kind]}{counter[kind]:03d}"
            name, *values = fields(i)
            add(kind, key, f"{name} ({label})", *values)
            keys.append(key)
        U[kind] = keys
        return keys

    def pick(source, n):
        """The next n of a shuffled source list, cycling."""
        order = list(source)
        R.shuffle(order)
        return [order[i % len(order)] for i in range(n)]

    n = lambda x: max(1, round(x * scale))

    # business layer
    titles = pick([f"{t} {d}" for d in DEPARTMENTS for t in TITLES], n(25))
    make("BusinessActor", 25, lambda i: (titles[i], "external" if i < 3 else "internal", DEPARTMENTS[i % 10]))
    roles = pick([f"{a} {k}" for a in AREAS for k in ROLE_KINDS], n(15))
    make("BusinessRole", 15, lambda i: (roles[i], f"Looks after {AREAS[i % 10].lower()} work."))
    procs = pick([f"{v} {o}" for v in VERBS for o in OBJECTS], n(60))
    make("BusinessProcess", 60, lambda i: (procs[i], weighted([("continuous", 3), ("daily", 4), ("weekly", 3), ("monthly", 2),
                                                               ("ad_hoc", 1)]), DEPARTMENTS[R.randrange(10)]))
    svcs = pick([f"{a} {k}" for a in AREAS for k in SERVICE_KINDS], n(12))
    make("BusinessService", 12, lambda i: (f"{svcs[i]} Service", R.choice(["online", "in_store", "phone", "any"])))
    objs = pick([f"{a} {k}" for a in AREAS for k in OBJECT_KINDS], n(20))
    make("BusinessObject", 20, lambda i: (objs[i], R.choice(["2 years", "3 years", "7 years", "10 years"])))

    # application layer
    prods = pick([f"{p} {k}" for p in PRODUCTS for k in PRODUCT_KINDS], n(45))
    make("ApplicationComponent", 45, lambda i: (prods[i], weighted([("in_house", 6), ("third_party", 4)]),
                                                weighted([("active", 8), ("legacy", 2), ("planned", 1)])))
    asvc = pick([f"{a} {v} Service" for a in AREAS for v in SERVICE_VERBS], n(40))
    make("ApplicationService", 40, lambda i: (asvc[i], weighted([("24x7", 7), ("business_hours", 3)])))
    data = pick([f"{a} {k}" for a in AREAS for k in DATA_KINDS], n(50))
    make("DataObject", 50, lambda i: (data[i], weighted([("public", 1), ("internal", 5), ("confidential", 3), ("restricted", 2)])))

    # technology layer
    make("TechnologyNode", 40, lambda i: (f"{NODE_KINDS[i % 8]} {i // 8 + 1:02d}",
                                         weighted([("production", 6), ("staging", 2), ("dr", 2)]),
                                         weighted([("cloud", 6), ("on_prem", 4)])))
    make("TechnologyService", 10, lambda i: (f"{TECH_SERVICES[i % 10]} Service", "Internal Platform Team"))

    # strategy
    res = pick(RESOURCES, n(15))
    make("Resource", 15, lambda i: (res[i][0], res[i][1]))
    caps = pick([f"{a} {k}" for a in AREAS for k in CAPABILITY_KINDS], n(25))
    make("Capability", 25, lambda i: (caps[i], weighted([("initial", 1), ("developing", 3), ("defined", 4), ("managed", 3),
                                                         ("optimising", 1)]), weighted([("low", 2), ("medium", 4), ("high", 3)])))
    streams = pick(STREAMS, n(4))
    make("ValueStream", 4, lambda i: (streams[i], R.choice(["true", "false"])))
    inits = pick([t.format(a) for t in INITIATIVES for a in AREAS], n(6))
    make("CourseOfAction", 6, lambda i: (inits[i], weighted([("proposed", 2), ("approved", 2), ("in_flight", 3), ("done", 2)]),
                                         R.choice(["now", "next", "later"])))

    # motivation
    stk = pick(STAKEHOLDERS, n(10))
    make("Stakeholder", 10, lambda i: (stk[i][0], stk[i][1], R.choice(["low", "medium", "high"])))
    drv = pick(DRIVERS, n(8))
    make("Driver", 8, lambda i: (drv[i][0], drv[i][1]))
    ass = pick([(t.format(a), r) for t, r in ASSESSMENTS for a in AREAS], n(10))
    make("Assessment", 10, lambda i: (ass[i][0], ass[i][1]))
    gls = pick([t.format(a) for t in GOALS for a in AREAS], n(10))
    make("Goal", 10, lambda i: (gls[i], weighted([("low", 1), ("medium", 3), ("high", 4), ("critical", 2)])))

    def outcome(i):
        t = OUTCOME_TEMPLATES[i % 4]
        a, pct = AREAS[R.randrange(10)], R.choice([5, 10, 15, 20, 25, 30, 40])
        text = t.format(pct, a) if t.startswith("Reach") else t.format(a, pct)
        return text, (dt.date(2027, 1, 31) + dt.timedelta(days=30 * R.randrange(0, 36))).isoformat()
    make("Outcome", 12, outcome)

    def requirement(i):
        t = REQUIREMENTS[R.randrange(8)]
        kind = next(v for k, v in REQUIREMENT_KINDS.items() if k in t)
        return t.format(AREAS[R.randrange(10)] + " " + R.choice(["Capture", "Lookup", "Reporting", "Handling", "Sync"])), kind, \
            weighted([("must", 4), ("should", 4), ("could", 2)])
    make("Requirement", 40, requirement)
    cons = pick([(t.format(a), k) for t, k in CONSTRAINTS for a in AREAS], n(8))
    make("Constraint", 8, lambda i: (cons[i][0], cons[i][1]))
    mean = pick(MEANINGS, n(5))
    make("Meaning", 5, lambda i: (mean[i],))

    G = grp or {}
    # business layer links
    fan("actor_roles.csv", U["BusinessActor"], U["BusinessRole"], 2, 4)
    fan("role_processes.csv", U["BusinessProcess"], U["BusinessRole"], 3, 6, reverse=True)
    fan("process_realizes_service.csv", U["BusinessProcess"], U["BusinessService"], 2, 4)
    fan("process_triggers.csv", U["BusinessProcess"], U["BusinessProcess"], 4, 7)
    fan("process_accesses_object.csv", U["BusinessProcess"], U["BusinessObject"], 5, 9, G.get("BusinessObject"), 0.1,
        value=lambda: R.choice(ACCESS))
    fan("business_service_serves_actor.csv", U["BusinessActor"], U["BusinessService"], 2, 4, reverse=True)
    # application layer links
    fan("app_service_serves_process.csv", U["BusinessProcess"], U["ApplicationService"], 8, 14, G.get("ApplicationService"), 0.3,
        reverse=True)
    fan("component_realizes_service.csv", U["ApplicationService"], U["ApplicationComponent"], 2, 4, G.get("ApplicationComponent"),
        0.15, reverse=True)
    fan("component_accesses_data.csv", U["ApplicationComponent"], U["DataObject"], 10, 16, G.get("DataObject"), 0.1,
        value=lambda: R.choice(ACCESS))
    fan("data_realizes_object.csv", U["DataObject"], U["BusinessObject"], 3, 4)
    fan("component_flows_to_component.csv", U["ApplicationComponent"], U["ApplicationComponent"], 10, 16,
        G.get("ApplicationComponent"), 0.25, value=lambda: R.choice(FREQUENCIES))
    # technology layer links
    fan("component_deployed_on_node.csv", U["ApplicationComponent"], U["TechnologyNode"], 5, 9, G.get("TechnologyNode"), 0.1)
    fan("techservice_serves_component.csv", U["ApplicationComponent"], U["TechnologyService"], 4, 7, G.get("TechnologyService"),
        0.3, reverse=True)
    fan("node_realizes_techservice.csv", U["TechnologyNode"], U["TechnologyService"], 2, 4, G.get("TechnologyService"), 0.2)
    # strategy links: the capabilities form a tree of three levels, and what realizes them is real work and real things
    caps_ = U["Capability"]
    top, mid, low = caps_[:5], caps_[5:15], caps_[15:]
    for i, c in enumerate(mid):
        link("capability_composition.csv", top[i % len(top)], c)
    for i, c in enumerate(low):
        link("capability_composition.csv", mid[i % len(mid)], c)
    fan("process_realizes_capability.csv", U["BusinessProcess"], caps_, 1, 3, G.get("Capability"), 0.1)
    fan("resource_realizes_capability.csv", caps_, U["Resource"], 3, 6, G.get("Resource"), 0.2, reverse=True)
    fan("component_realizes_resource.csv", U["Resource"], U["ApplicationComponent"], 2, 4, reverse=True)
    fan("node_realizes_resource.csv", U["Resource"], U["TechnologyNode"], 3, 6, reverse=True)
    fan("capability_serves_value_stream.csv", U["ValueStream"], caps_, 8, 14, G.get("Capability"), 0.1, reverse=True)
    fan("value_stream_serves_actor.csv", U["ValueStream"], U["BusinessActor"], 5, 8, G.get("BusinessActor"), 0.1, reverse=True)
    fan("course_aggregates_capability.csv", U["CourseOfAction"], caps_, 6, 12, G.get("Capability"), 0.1)
    fan("course_realizes_goal.csv", U["CourseOfAction"], U["Goal"], 2, 4, G.get("Goal"), 0.2)
    fan("capability_realizes_requirement.csv", U["Requirement"], caps_, 2, 4, reverse=True)
    # motivation links
    fan("stakeholder_driver.csv", U["Driver"], U["Stakeholder"], 2, 4, reverse=True)
    fan("actor_stakeholder.csv", U["Stakeholder"], U["BusinessActor"], 3, 6, reverse=True)
    fan("driver_influences_driver.csv", U["Driver"], U["Driver"], 2, 3, G.get("Driver"), 0.3, value=lambda: R.choice(STRENGTHS))
    fan("driver_assessment.csv", U["Assessment"], U["Driver"], 1, 2, reverse=True)
    fan("assessment_influences_goal.csv", U["Assessment"], U["Goal"], 2, 4, value=lambda: R.choice(STRENGTHS))
    for i, g in enumerate(U["Goal"][3:], 3):
        link("goal_aggregation.csv", U["Goal"][i % 3], g)
    fan("outcome_realizes_goal.csv", U["Outcome"], U["Goal"], 1, 3)
    fan("principle_realizes_goal.csv", U["Goal"], G.get("Principle", []), 1, 2, reverse=True)
    fan("requirement_realizes_outcome.csv", U["Requirement"], U["Outcome"], 1, 3)
    fan("requirement_realizes_principle.csv", U["Requirement"], G.get("Principle", []), 1, 2)
    fan("constraint_influences_requirement.csv", U["Constraint"], U["Requirement"], 4, 8, value=lambda: R.choice(STRENGTHS))
    for file, kind, count in [("process_realizes_requirement.csv", "BusinessProcess", (3, 4)),
                              ("business_service_realizes_requirement.csv", "BusinessService", (1, 2)),
                              ("app_service_realizes_requirement.csv", "ApplicationService", (3, 5)),
                              ("component_realizes_requirement.csv", "ApplicationComponent", (3, 5)),
                              ("node_realizes_requirement.csv", "TechnologyNode", (1, 3))]:
        fan(file, U["Requirement"], U[kind], count[0], count[1], G.get(kind), 0.1, reverse=True)
    fan("meaning_object.csv", U["Meaning"], U["BusinessObject"], 3, 6)
    fan("business_service_value.csv", U["BusinessService"], G.get("Value", []), 1, 2)
    fan("stakeholder_value.csv", U["Stakeholder"], G.get("Value", []), 2, 3)
    return U


def build_group():
    """The group's shared elements: the hand-written core, and a generated layer of shared platforms and direction."""
    for kind, (file, _) in TYPES.items():
        rows[kind].extend([[r["id"], r["name"], *[r.get(c, "") for c in TYPES[kind][1][2:]]] for r in read_core(file)])
    for file, cols in RELS.items():
        for r in read_core(file):
            link(file, r[cols[0]], r[cols[1]], *[r[c] for c in cols[2:]])
    G = {kind: [r[0] for r in rows[kind]] for kind in TYPES}
    # the group-level elements the units link out to
    for i, (name, rationale) in enumerate(PRINCIPLES):
        G["Principle"].append(add("Principle", f"grp-pri{i + 1:03d}", name, rationale))
    for i, (name, kind) in enumerate(VALUES):
        G["Value"].append(add("Value", f"grp-val{i + 1:03d}", name, kind))
    for i, (name, kind) in enumerate(DRIVERS):
        G["Driver"].append(add("Driver", f"grp-drv{i + 1:03d}", f"{name} (Group)", kind))
    for i, area in enumerate(AREAS):
        G["Goal"].append(add("Goal", f"grp-gol{i + 1:03d}", f"Be the easiest retailer for {area.lower()}", "critical"))
    for i in range(40):
        G["ApplicationComponent"].append(add("ApplicationComponent", f"grp-cmp{i + 1:03d}",
                                             f"{PRODUCTS[i % 20]} {PRODUCT_KINDS[i // 20 * 4 % 8]} (Group)",
                                             R.choice(["in_house", "third_party"]), "active"))
    for i in range(30):
        G["ApplicationService"].append(add("ApplicationService", f"grp-asv{i + 1:03d}",
                                           f"{AREAS[i % 10]} {SERVICE_VERBS[i // 10]} Service (Group)", "24x7"))
    for i in range(30):
        G["DataObject"].append(add("DataObject", f"grp-dat{i + 1:03d}", f"{AREAS[i % 10]} {DATA_KINDS[i // 10]} (Group)",
                                   R.choice(["internal", "confidential"])))
    for i in range(20):
        G["TechnologyNode"].append(add("TechnologyNode", f"grp-nod{i + 1:03d}", f"{NODE_KINDS[i % 8]} G{i // 8 + 1:02d} (Group)",
                                       "production", "cloud"))
    for i in range(10):
        G["TechnologyService"].append(add("TechnologyService", f"grp-tsv{i + 1:03d}", f"{TECH_SERVICES[i]} Platform (Group)",
                                          "Group Platform Team"))
    for i in range(8):
        G["BusinessObject"].append(add("BusinessObject", f"grp-bob{i + 1:03d}", f"{AREAS[i]} Master Record (Group)", "10 years"))
    for i in range(15):
        G["Capability"].append(add("Capability", f"grp-cap{i + 1:03d}", f"Group {AREAS[i % 10]} {CAPABILITY_KINDS[i // 10]}",
                                   "managed", "high"))
    for i in range(10):
        G["Resource"].append(add("Resource", f"grp-res{i + 1:03d}", f"{RESOURCES[i][0]} (Group)", RESOURCES[i][1]))
    for i, a in enumerate(AREAS[:4]):
        G["BusinessActor"].append(add("BusinessActor", f"grp-act{i + 1:03d}", f"Group {DEPARTMENTS[i]} Director (Group)",
                                      "internal", DEPARTMENTS[i]))
    # the shared components run on shared nodes and use shared services
    fan("component_deployed_on_node.csv", G["ApplicationComponent"][-40:], G["TechnologyNode"], 3, 5)
    fan("techservice_serves_component.csv", G["ApplicationComponent"][-40:], G["TechnologyService"], 3, 5, reverse=True)
    fan("component_realizes_service.csv", G["ApplicationService"][-30:], G["ApplicationComponent"][-40:], 2, 3, reverse=True)
    fan("node_realizes_techservice.csv", G["TechnologyNode"][-20:], G["TechnologyService"], 2, 3)
    return G


G = build_group()
for market, mname in MARKETS:
    for line, lname in LINES:
        build(f"{market}{line}", f"{market.upper()} {lname}", G)

# group goals are made of the units' goals, and units' goals sit in the group's
unit_goals = [r[0] for r in rows["Goal"] if not r[0].startswith("grp-")]
for i, g in enumerate(unit_goals):
    link("goal_aggregation.csv", G["Goal"][-10:][i % 10], g)

os.makedirs(OUT, exist_ok=True)
total = 0
for kind, (file, cols) in TYPES.items():
    with open(os.path.join(OUT, file), "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(cols)
        w.writerows(rows[kind])
for file, cols in RELS.items():
    with open(os.path.join(OUT, file), "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(cols)
        w.writerows(links[file])
    total += len(links[file])
print(f"{sum(len(r) for r in rows.values())} elements, {total} relationships")
for kind in TYPES:
    print(f"  {kind}: {len(rows[kind])}")
for file in RELS:
    print(f"  {file}: {len(links[file])}")
