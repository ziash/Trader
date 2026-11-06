// Names for the API's data, from the generated contract types (npm run gen:api).
import type { components } from "./schema";

type S = components["schemas"];
export type Role = S["Role"];
export type Me = S["Me"];
export type Status = S["Status"];
export type StatusUnit = NonNullable<Status["units"]>[number];
export type StatusEvent = NonNullable<Status["events"]>[number];
export type ControlChange = NonNullable<Status["controls"]>[number];
export type UsersOverview = S["UsersOverview"];
export type LogRow = UsersOverview["log"][number];
export type Account = NonNullable<Status["accounts"]>[number];
export type ScoutDay = S["ScoutDay"];
export type ScoutSetup = S["ScoutSetup"];
export type ScoutOrder = S["ScoutOrder"];
export type TimelineItem = ScoutSetup["timeline"][number];
export type AccountView = S["AccountView"];
export type AccountStatus = S["AccountStatus"];
export type TradesDay = S["TradesDay"];
export type Trade = S["Trade"];
export type TradeEvent = S["TradeEvent"];
export type OpenPosition = S["OpenPosition"];
export type OpenOrder = S["OpenOrder"];
export type ConditionsView = S["ConditionsView"];
export type ConditionsRow = S["ConditionsRow"];
export type MarginPoint = S["MarginPoint"];
export type AuditView = S["AuditView"];
export type AuditFigures = S["AuditFigures"];
