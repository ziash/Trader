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
