# Using the inventoryzing web app

Start with the [installation guide](../../README.md#get-running), then open [http://localhost:8088](http://localhost:8088). The web app is included in the Docker installation.

## Browse and find items

**Inventory** opens as a tree. Expand a branch to see its contents, click a name to open its record, or click the tree icon beside an object to focus on that location. Objects created in a focused subtree default to that parent.

Search within the current subtree or return to the main inventory to search everywhere. Combine text with type and tag filters. **All objects** shows a flat list. Click any segment of a location path to open that location.

On an object record, use **Move** to select a new destination. Check **Contents** for nested items and **History** for previous changes.

## Add items quickly

Use **Quick create** for repeated intake. Choose the type and destination, enter the name and properties, and click **Add**. Copying an existing object replaces the intake inputs; it does not create or print anything until you add the item.

Inputs and recent activity are saved in this browser for your account. The label preview uses a sample until an object has been created, then shows the last created object.

## Types, tags, and properties

Use **Object types** to group similar items and set shared properties. Types can inherit from a parent type. On a property, choose **Use inherited value**, **Set a local value**, or **Leave unset**. Leaving a property unset suppresses its inherited value. Quantity properties support units; **Convert display** changes the displayed unit.

Use **Tags** to classify types and individual objects. A tag can have multiple parents, but cannot inherit from its own descendants. Objects inherit their type's tags and the ancestors of assigned tags. The object's tag display identifies where inherited tags came from.

**Available properties** lists property names, keys, and examples. The list includes fields supplied by enabled features. Values vary by object; missing values appear blank on labels. When creating a property, leave the internal key blank to generate it automatically.

## Track stock

Configure a type's stock policy to choose its measurement, input unit, smallest increment, and whether negative quantities are allowed. The smallest increment uses the measurement's canonical unit. A child type can inherit a policy; saving its own policy overrides that inheritance.

Create a stock holding through **Quick create** and enter its initial quantity. Use the stock controls on its record to adjust the amount and record a reason.

## Scan items

Use the scan input to look up an inventory identifier. For a connected Zebra scanner, follow the [scanner setup guide](../../agents/windows/inventoryzing-agent/README.md#run-the-live-scanner-agent).

In **Scanner**, scan a label to open its object record. Lookup remains active while you browse. To move several items, open the destination's **Move into here** workflow, scan each item, and select **Finish** when done. Manual input performs the same action as a hardware scan.

Only one tab controls a scanner terminal at a time. Use **Take control of this terminal** if you need to switch tabs. If delivery fails, scan again; failed scans are not queued for later.

## Design and print labels

Open **Label templates**, choose or create a template, and select the roll size. Add text, properties, QR codes, or branding, then drag elements into position. **Save changes**, preview with a real inventory object, and **Publish revision** when ready to use the design.

Use **Placeholders** to find available fields. Text and QR content can include keys in braces, such as `{object.name}`. Expressions support indexes and slices: `{object.uuid[-8:]}` inserts the last eight UUID characters. `{labeling.printed_at}` inserts the printing time in UTC. Missing values are blank.

Text wrapping breaks at words and shortens the last visible line with an ellipsis. Shrink-to-fit reduces the font size to fit the element. Designs are clipped at the shaded printable margins and are not automatically resized to fit a roll. The full-height option for 23 mm labels uses the full feed direction for layout while retaining normal cutting.

From an object record, choose a template and **Print tag**, or download its QR SVG/use browser printing. Direct printing requires the [printer agent](../../agents/windows/inventoryzing-agent/README.md#run-the-first-managed-tag).

Only one direct print request can be active at a time. Restarting the app or agent loses the outstanding request. Before retrying an uncertain print, check the printer. **Force reset printer** clears the request, but a label already sent to the printer may still print.

## Administration

Your permissions determine which administration pages and actions are available.

- **Accounts and roles:** create sign-in accounts and assign roles. A role is a reusable set of permissions; an account receives the combined permissions of all its roles. Passwords must contain at least 12 characters.
- **Principals:** manage people, teams, organizations, projects, and sites separately from sign-in accounts. A principal linked to an account must remain a person.
- **Site settings:** change the site name and upload a PNG or JPEG logo for label branding. Labels use the site name when no logo is configured.

## Running the web app separately

For a local frontend server, backend configuration, and build commands, see [Development and verification](../../docs/development.md). A separate frontend server is not needed for the Docker installation.
