import { expect, test } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { randomUUID } from 'node:crypto'
import type { CommandResult, Session } from '../src/api'

test('create, scan, move, history and printable label', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/')
  await page.getByRole('textbox', { name: 'Login', exact: true }).fill('browser-test')
  await page.getByLabel(/^Password/).fill('browser-test-only-password')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Inventory' })).toBeVisible()
  const suffix = `${testInfo.project.name}-${Date.now()}`
  const shelf = `Shelf ${suffix}`
  const item = `Multimeter ${suffix}`
  async function create(name: string) {
    await page.getByRole('link', { name: 'Inventory', exact: true }).first().click()
    await page.getByRole('button', { name: 'New object', exact: true }).click()
    await page.getByRole('textbox', { name: 'Name', exact: true }).fill(name)
    await page.getByLabel('Description', { exact: true }).fill('Calibrated workshop equipment')
    await page.getByRole('button', { name: 'Create object', exact: true }).click()
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible()
  }
  await create(shelf)
  await create(item)
  const label = page.getByRole('img', { name: `QR label for ${item}` })
  await expect(label).toBeVisible()
  await expect.poll(() => label.evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0)
  const alias = await page.locator('.label-value').innerText()
  await page.getByRole('link', { name: 'Inventory', exact: true }).first().click()
  await page.getByRole('textbox', { name: 'Scan identifier' }).fill(alias)
  await page.getByRole('textbox', { name: 'Scan identifier' }).press('Enter')
  await expect(page.getByRole('heading', { name: item, exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Move', exact: true }).click()
  await page.getByRole('combobox', { name: 'Destination', exact: true }).fill(shelf)
  await page.getByRole('option', { name: new RegExp(shelf) }).click()
  await page.getByRole('button', { name: 'Move object', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.locator('.record-fields').getByRole('link', { name: shelf })).toBeVisible()
  await page.getByRole('tab', { name: 'History', exact: true }).click()
  await expect(page.getByText('Object moved', { exact: true })).toBeVisible()
  await expect(page.getByText('Object created', { exact: true })).toBeVisible()
  await page.getByRole('tab', { name: 'Overview', exact: true }).click()
  const accessibility = await new AxeBuilder({ page }).analyze()
  expect(accessibility.violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('object-detail.png'), fullPage: true })
  expect(errors).toEqual([])
})

test('lost command response can be retried after reload without duplicating state', async ({ page }, testInfo) => {
  await page.goto('/')
  await page.getByRole('textbox', { name: 'Login', exact: true }).fill('browser-test')
  await page.getByLabel(/^Password/).fill('browser-test-only-password')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  const name = `Retry ${testInfo.project.name}-${Date.now()}`
  const envelopes: string[] = []
  await page.route('**/api/commands', async route => {
    envelopes.push(route.request().postData() ?? '')
    const response = await route.fetch()
    if (envelopes.length === 1) await route.abort('connectionfailed')
    else await route.fulfill({ response })
  })
  await page.getByRole('button', { name: 'New object', exact: true }).click()
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill(name)
  await page.getByRole('button', { name: 'Create object', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Retry original command' }).last()).toBeVisible()
  await page.reload()
  await page.getByRole('button', { name: 'Retry original command' }).click()
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible()
  expect(envelopes).toHaveLength(2)
  expect(envelopes[0]).toBe(envelopes[1])
  await page.getByRole('link', { name: 'Inventory', exact: true }).first().click()
  await page.getByRole('textbox', { name: 'Search inventory' }).fill(name)
  await expect(page.locator('tbody tr')).toHaveCount(1)
})

test('hierarchical tags classify types and objects with inherited filtering', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/')
  await page.getByRole('textbox', { name: 'Login', exact: true }).fill('browser-test')
  await page.getByLabel(/^Password/).fill('browser-test-only-password')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  const suffix = `${testInfo.project.name}-${Date.now()}`
  const hardware = `Hardware ${suffix}`
  const nut = `Nut ${suffix}`
  const slotNut = `M5 Slot Nut ${suffix}`
  const needsRepair = `Needs Repair ${suffix}`
  const typeName = `DIN 508 T-nut ${suffix}`
  const correctedTypeName = `Unclassified hardware ${suffix}`
  const objectName = `T-nut stock ${suffix}`

  await page.getByRole('link', { name: 'Tags', exact: true }).click()
  async function createTag(name: string, parents: string[] = []) {
    await page.getByRole('button', { name: 'New tag', exact: true }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill(name)
    for (const parent of parents) {
      const picker = dialog.getByRole('combobox', { name: 'Parent tags', exact: true })
      await picker.fill(parent)
      await page.getByRole('option', { name: parent, exact: true }).click()
      await picker.press('Escape')
    }
    await dialog.getByRole('button', { name: 'Create tag', exact: true }).click()
    await expect(dialog).toHaveCount(0)
  }
  await createTag(hardware)
  await createTag(nut)
  await createTag(slotNut, [hardware, nut])
  await createTag(needsRepair)

  await page.getByRole('button', { name: `Expand ${hardware}`, exact: true }).click()
  await expect(page.getByText(slotNut, { exact: true })).toBeVisible()
  await page.getByRole('button', { name: `Expand ${nut}`, exact: true }).click()
  await expect(page.getByText(slotNut, { exact: true })).toHaveCount(2)

  await page.getByRole('link', { name: 'Object types', exact: true }).click()
  await page.getByRole('button', { name: 'New type', exact: true }).click()
  let dialog = page.getByRole('dialog')
  await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill(typeName)
  await dialog.getByRole('combobox', { name: 'Tags', exact: true }).fill(slotNut)
  await page.getByRole('option', { name: slotNut, exact: true }).click()
  await dialog.getByRole('combobox', { name: 'Tags', exact: true }).press('Escape')
  await dialog.getByRole('button', { name: 'Create type', exact: true }).click()
  await expect(dialog).toHaveCount(0)
  const typeRow = page.getByRole('row').filter({ hasText: typeName })
  await expect(typeRow.getByText(slotNut, { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'New type', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill(correctedTypeName)
  await dialog.getByRole('button', { name: 'Create type', exact: true }).click()
  await expect(dialog).toHaveCount(0)

  await page.getByRole('link', { name: 'Inventory', exact: true }).first().click()
  await page.getByRole('button', { name: 'New object', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill(objectName)
  await dialog.getByRole('combobox', { name: 'Object type', exact: true }).fill(typeName)
  await page.getByRole('option', { name: typeName, exact: true }).click()
  await dialog.getByRole('combobox', { name: 'Tags', exact: true }).fill(needsRepair)
  await page.getByRole('option', { name: needsRepair, exact: true }).click()
  await dialog.getByRole('combobox', { name: 'Tags', exact: true }).press('Escape')
  await dialog.getByRole('button', { name: 'Create object', exact: true }).click()
  await expect(dialog).toHaveCount(0)
  const tagRecord = page.locator('.record-fields dd').filter({ has: page.locator('.tag-summary') })
  await expect(tagRecord.getByText(slotNut, { exact: true })).toBeVisible()
  await expect(tagRecord.getByText(hardware, { exact: true })).toBeVisible()
  await expect(tagRecord.getByText(nut, { exact: true })).toBeVisible()
  await expect(tagRecord.getByText(needsRepair, { exact: true })).toBeVisible()
  await expect(tagRecord.getByText('Inherited from object type', { exact: true })).toBeVisible()
  await expect(tagRecord.getByText('Assigned directly', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Change object type', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.getByRole('combobox', { name: 'Object type', exact: true }).fill(correctedTypeName)
  await page.getByRole('option', { name: correctedTypeName, exact: true }).click()
  await dialog.getByRole('button', { name: 'Save object type', exact: true }).click()
  await expect(dialog).toHaveCount(0)
  await expect(page.getByText(correctedTypeName, { exact: true })).toBeVisible()
  await expect(tagRecord.getByText(needsRepair, { exact: true })).toBeVisible()
  await expect(tagRecord.getByText(slotNut, { exact: true })).toHaveCount(0)

  await page.getByRole('button', { name: 'Change object type', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.getByRole('combobox', { name: 'Object type', exact: true }).fill(typeName)
  await page.getByRole('option', { name: typeName, exact: true }).click()
  await dialog.getByRole('button', { name: 'Save object type', exact: true }).click()
  await expect(dialog).toHaveCount(0)
  await expect(page.getByText(typeName, { exact: true })).toBeVisible()
  await expect(tagRecord.getByText(slotNut, { exact: true })).toBeVisible()

  await tagRecord.getByRole('link', { name: hardware, exact: true }).click()
  await expect(page).toHaveURL(/\/tag\/[^/]+$/)
  await expect(page.getByRole('heading', { level: 1 })).toContainText(hardware)
  await expect(page.getByRole('row').filter({ hasText: objectName })).toBeVisible()
  await page.getByRole('combobox', { name: 'Filter by object type', exact: true }).fill(typeName)
  await page.getByRole('option', { name: typeName, exact: true }).click()
  await expect(page.getByRole('row').filter({ hasText: objectName })).toBeVisible()

  await page.getByRole('link', { name: 'Tags', exact: true }).click()
  const hardwareRow = page.locator(`[data-tag-id]`).filter({ hasText: hardware }).first()
  await hardwareRow.getByRole('button', { name: `Manage parents of ${hardware}`, exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.getByRole('combobox', { name: 'Parent tags', exact: true }).fill(slotNut)
  await page.getByRole('option', { name: slotNut, exact: true }).click()
  await dialog.getByRole('combobox', { name: 'Parent tags', exact: true }).press('Escape')
  await dialog.getByRole('button', { name: 'Save parents', exact: true }).click()
  await expect(dialog.getByRole('alert')).toContainText('cannot inherit from itself or one of its descendants')
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)

  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('classification.png'), fullPage: true })
  expect(errors).toEqual([])
})

test('shared branding, expandable hierarchy, focused subtrees and full location paths', async ({ page }, testInfo) => {
  test.setTimeout(60_000)
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/')
  const logo = page.getByRole('img', { name: 'inventoryzing', exact: true })
  await expect(logo).toBeVisible()
  await expect.poll(() => logo.evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0)
  await page.screenshot({ path: testInfo.outputPath('sign-in-brand.png'), fullPage: true })
  await page.getByRole('textbox', { name: 'Login', exact: true }).fill('browser-test')
  await page.getByLabel(/^Password/).fill('browser-test-only-password')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Inventory' })).toBeVisible()
  const session = await (await page.request.get('/api/auth/session')).json() as Session
  const suffix = `${testInfo.project.name}-${Date.now()}`
  async function create(name: string, parentId: string | null = null) {
    const response = await page.request.post('/api/commands', {
      headers: { Origin: new URL(page.url()).origin, 'X-CSRF-Token': session.csrf_token },
      data: { authority_site: session.site_id, authority_epoch: 1, command_epoch: session.command_epoch,
        command_id: randomUUID(), payload: { kind: 'object.create', name, parent_id: parentId } },
    })
    expect(response.ok(), await response.text()).toBe(true)
    return (await response.json() as CommandResult).entity_id
  }
  const workshopName = `Electronics and instrumentation workshop ${suffix}`
  const rackName = 'Rack A - Test and measurement equipment'
  const drawerName = 'Drawer 04 - Portable instruments and accessories'
  const meterName = `Handheld multimeter ${suffix}`
  const workshop = await create(workshopName)
  const rack = await create(rackName, workshop)
  const drawer = await create(drawerName, rack)
  const meter = await create(meterName, drawer)
  await create(`Multimeter outside ${suffix}`)
  await page.getByRole('button', { name: 'Refresh inventory' }).click()
  const expandWorkshop = page.getByRole('button', { name: `Expand ${workshopName}`, exact: true })
  await expect(expandWorkshop).toHaveAttribute('aria-expanded', 'false')
  await expandWorkshop.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('button', { name: `Collapse ${workshopName}`, exact: true })).toHaveAttribute('aria-expanded', 'true')
  await expect(page.getByRole('button', { name: `Expand ${rackName}`, exact: true })).toBeVisible()
  await expect(page.locator(`[data-object-id="${drawer}"]`)).toHaveCount(0)
  await page.getByRole('button', { name: `Collapse ${workshopName}`, exact: true }).click()
  await expect(page.locator(`[data-object-id="${rack}"]`)).toHaveCount(0)
  await page.getByRole('button', { name: `Expand ${workshopName}`, exact: true }).click()
  await page.getByRole('link', { name: `View subtree of ${rackName}`, exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/tree/${rack}$`))
  await expect(page.getByRole('heading', { name: rackName, exact: true })).toBeVisible()
  await expect(page.locator(`[data-object-id="${workshop}"]`)).toHaveCount(0)
  await page.route(`**/api/objects?parent_id=${drawer}&offset=0`, route => route.abort(), { times: 1 })
  await page.getByRole('button', { name: `Expand ${drawerName}`, exact: true }).click()
  const drawerNode = page.locator(`[data-object-id="${drawer}"]`)
  await expect(drawerNode.getByRole('alert')).toBeVisible()
  await drawerNode.getByRole('button', { name: 'Retry', exact: true }).click()
  await expect(page.locator(`[data-object-id="${meter}"]`)).toBeVisible()
  await expect(page.locator(`[data-object-id="${meter}"]`).getByRole('button', { name: /^Expand/ })).toHaveCount(0)
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('expanded-subtree.png'), fullPage: true })
  await page.getByRole('textbox', { name: 'Search inventory' }).fill('multimeter')
  await expect(page.locator('tbody tr')).toHaveCount(1)
  await expect(page.locator('tbody .location-path')).toContainText(`${session.site_name} / ${workshopName} / ${rackName} / ${drawerName}`)
  await page.locator('tbody .object-link').click()
  await expect(page.getByRole('heading', { name: meterName, exact: true })).toBeVisible()
  await expect(page.locator('.record-fields .location-path')).toHaveText(`${session.site_name} / ${workshopName} / ${rackName} / ${drawerName}`)
  if (testInfo.project.name === 'mobile') {
    const heading = await page.getByRole('heading', { name: meterName, exact: true }).boundingBox()
    const moveButton = await page.getByRole('button', { name: 'Move', exact: true }).boundingBox()
    expect(heading).not.toBeNull()
    expect(moveButton).not.toBeNull()
    expect(moveButton!.y).toBeGreaterThanOrEqual(heading!.y + heading!.height)
  }
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('full-location-path.png'), fullPage: true })
  await page.locator('.record-fields').getByRole('link', { name: rackName, exact: true }).click()
  await page.getByRole('tab', { name: 'Contents', exact: true }).click()
  await page.getByRole('button', { name: `Expand ${drawerName}`, exact: true }).click()
  await expect(page.locator(`[data-object-id="${meter}"]`)).toBeVisible()
  for (let index = 0; index < 51; index++) await create(`Part ${String(index).padStart(2, '0')}`, drawer)
  await page.goto(`/tree/${drawer}`)
  const children = page.getByRole('list', { name: `Contents of ${drawerName}`, exact: true })
  await expect(children.locator(':scope > li[data-object-id]')).toHaveCount(50)
  await page.getByRole('button', { name: `Load more children of ${drawerName}`, exact: true }).click()
  await expect(children.locator(':scope > li[data-object-id]')).toHaveCount(52)
  expect(await children.locator(':scope > li[data-object-id]').evaluateAll(nodes => new Set(nodes.map(node => node.getAttribute('data-object-id'))).size)).toBe(52)
  await page.getByRole('button', { name: 'New object', exact: true }).click()
  await expect(page.getByRole('combobox', { name: 'Parent object', exact: true })).toHaveValue(new RegExp(drawerName))
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill(`Created inside subtree ${suffix}`)
  await page.getByRole('button', { name: 'Create object', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.locator('.record-fields .location-path')).toContainText(drawerName)
  expect(errors).toEqual([])
})