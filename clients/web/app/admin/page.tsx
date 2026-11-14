"use client";

import { useState } from "react";
import Typography from "@mui/material/Typography";
import Box from "@mui/material/Box";
import Paper from "@mui/material/Paper";
import Button from "@mui/material/Button";
import IconButton from "@mui/material/IconButton";
import Alert from "@mui/material/Alert";
import Skeleton from "@mui/material/Skeleton";
import Chip from "@mui/material/Chip";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Dialog from "@mui/material/Dialog";
import DialogTitle from "@mui/material/DialogTitle";
import DialogContent from "@mui/material/DialogContent";
import DialogActions from "@mui/material/DialogActions";
import TextField from "@mui/material/TextField";
import MenuItem from "@mui/material/MenuItem";
import Tabs from "@mui/material/Tabs";
import Tab from "@mui/material/Tab";
import DeleteIcon from "@mui/icons-material/Delete";
import EditIcon from "@mui/icons-material/Edit";
import PersonRemoveIcon from "@mui/icons-material/PersonRemove";

import ErrorPanel from "@/components/ErrorPanel";
import { useMe } from "@/lib/me";
import {
  useAddMember,
  useCreateGroup,
  useCreateInvite,
  useCreateUser,
  useDeleteGroup,
  useGroupMembers,
  useRemoveMember,
  useRetention,
  useTenantAudit,
  useTenantGroups,
  useTenantUsers,
  useUpdateGroup,
  type Group,
  type InviteResult,
} from "@/lib/admin";

// LolaRole staff-assignable members — TenantAdmin/LoanOfficer grant
// console access; Borrower/Service aren't meaningful group roles.
const MEMBER_ROLE_CHOICES = ["LoanOfficer", "TenantAdmin"];

/// Admin (MPE-211) — users, groups + membership, borrower invites,
/// tenant audit, retention report. Reads admit staff; writes are
/// TenantAdmin-only (server enforces — the UI hides what the API
/// would reject anyway).
export default function AdminPage() {
  const { me, isStaff, isAdmin } = useMe();
  const tid = me?.tid;
  const [tab, setTab] = useState(0);

  const users = useTenantUsers(tid);
  const groups = useTenantGroups(tid);
  const [auditFilters, setAuditFilters] = useState({ actor: "", action: "", since: "" });
  const audit = useTenantAudit(tid, auditFilters, tab === 3);
  const retention = useRetention(tid, tab === 4);

  const createUser = useCreateUser(tid);
  const createGroup = useCreateGroup(tid);
  const updateGroup = useUpdateGroup(tid);
  const deleteGroup = useDeleteGroup(tid);
  const addMember = useAddMember(tid);
  const removeMember = useRemoveMember(tid);
  const createInvite = useCreateInvite(tid);

  const [userDialog, setUserDialog] = useState(false);
  const [userForm, setUserForm] = useState({ email: "", displayName: "", password: "" });
  const [groupDialog, setGroupDialog] = useState(false);
  const [groupForm, setGroupForm] = useState({ name: "", description: "", roles: [] as string[] });
  const [editGroup, setEditGroup] = useState<Group | null>(null);
  const [membersFor, setMembersFor] = useState<Group | null>(null);
  const [memberPick, setMemberPick] = useState("");
  const members = useGroupMembers(tid, membersFor?.id ?? null);
  const [inviteEmail, setInviteEmail] = useState("");
  const [inviteResult, setInviteResult] = useState<InviteResult | null>(null);
  const [pageError, setPageError] = useState<string | null>(null);

  if (!isStaff) {
    return <Alert severity="warning">Administrative surfaces are staff-only.</Alert>;
  }

  const fail = (label: string) => (e: unknown) => setPageError(`${label}: ${e}`);

  const doSaveGroup = () => {
    const body = {
      name: groupForm.name.trim(),
      description: groupForm.description || undefined,
      roles: groupForm.roles,
    };
    const done = (r: { status: number }) => {
      setGroupDialog(false);
      if (r.status >= 400) setPageError(`Save group failed: HTTP ${r.status}`);
    };
    if (editGroup) updateGroup.mutate({ gid: editGroup.id, ...body }, { onSuccess: done, onError: fail("Save group") });
    else createGroup.mutate(body, { onSuccess: done, onError: fail("Create group") });
  };

  const doCreateUser = () =>
    createUser.mutate(
      {
        email: userForm.email.trim(),
        displayName: userForm.displayName.trim(),
        password: userForm.password || undefined,
      },
      {
        onSuccess: (r) => {
          setUserDialog(false);
          if (r.status >= 400) setPageError(`Create user failed: HTTP ${r.status}`);
        },
        onError: fail("Create user"),
      },
    );

  const doInvite = () =>
    createInvite.mutate(inviteEmail.trim(), {
      onSuccess: (r) => {
        if (r.status < 400 && r.data) setInviteResult(r.data as InviteResult);
        else setPageError(`Invite failed: HTTP ${r.status}`);
      },
      onError: fail("Invite"),
    });

  return (
    <>
      <Typography variant="h6" sx={{ mb: 1.5 }}>
        Administration
      </Typography>
      {pageError && (
        <Alert severity="error" variant="outlined" sx={{ mb: 1.5 }} onClose={() => setPageError(null)}>
          {pageError}
        </Alert>
      )}

      <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ mb: 2 }}>
        <Tab label="Users" />
        <Tab label="Groups" />
        <Tab label="Borrower Invites" />
        <Tab label="Tenant Audit" />
        <Tab label="Retention" />
      </Tabs>

      {/* —— Users —— */}
      {tab === 0 && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Box sx={{ display: "flex", mb: 1 }}>
            <Typography variant="subtitle1" sx={{ flexGrow: 1 }}>
              Users
            </Typography>
            {isAdmin && (
              <Button
                size="small"
                variant="outlined"
                onClick={() => {
                  setUserForm({ email: "", displayName: "", password: "" });
                  setUserDialog(true);
                }}
              >
                Create user
              </Button>
            )}
          </Box>
          {users.isLoading && <Skeleton variant="rectangular" height={120} />}
          {users.isError && <ErrorPanel title="Users failed" error={users.error} />}
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell sx={{ pl: 0 }}>Email</TableCell>
                <TableCell>Name</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Roles</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {(users.data ?? []).map((u) => (
                <TableRow key={u.id}>
                  <TableCell sx={{ pl: 0 }}>{u.email}</TableCell>
                  <TableCell>{u.displayName}</TableCell>
                  <TableCell>
                    <Chip size="small" label={u.status} color={u.status === "active" ? "success" : "default"} variant="outlined" />
                  </TableCell>
                  <TableCell>{u.roles.join(", ") || "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Paper>
      )}

      {/* —— Groups —— */}
      {tab === 1 && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Box sx={{ display: "flex", mb: 1 }}>
            <Typography variant="subtitle1" sx={{ flexGrow: 1 }}>
              Groups
            </Typography>
            {isAdmin && (
              <Button
                size="small"
                variant="outlined"
                onClick={() => {
                  setEditGroup(null);
                  setGroupForm({ name: "", description: "", roles: [] });
                  setGroupDialog(true);
                }}
              >
                Create group
              </Button>
            )}
          </Box>
          {groups.isLoading && <Skeleton variant="rectangular" height={100} />}
          {groups.isError && <ErrorPanel title="Groups failed" error={groups.error} />}
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell sx={{ pl: 0 }}>Name</TableCell>
                <TableCell>Description</TableCell>
                <TableCell>Roles</TableCell>
                <TableCell align="right">Members</TableCell>
                <TableCell align="right" />
              </TableRow>
            </TableHead>
            <TableBody>
              {(groups.data ?? []).map((g) => (
                <TableRow key={g.id}>
                  <TableCell sx={{ pl: 0 }}>
                    <Button size="small" onClick={() => setMembersFor(g)}>
                      {g.name}
                    </Button>
                  </TableCell>
                  <TableCell>{g.description ?? "—"}</TableCell>
                  <TableCell>{g.roles.join(", ") || "—"}</TableCell>
                  <TableCell align="right">{g.memberCount}</TableCell>
                  <TableCell align="right">
                    {isAdmin && (
                      <>
                        <IconButton
                          size="small"
                          aria-label={`edit ${g.name}`}
                          onClick={() => {
                            setEditGroup(g);
                            setGroupForm({ name: g.name, description: g.description ?? "", roles: g.roles });
                            setGroupDialog(true);
                          }}
                        >
                          <EditIcon sx={{ fontSize: 15 }} />
                        </IconButton>
                        <IconButton
                          size="small"
                          aria-label={`delete ${g.name}`}
                          onClick={() =>
                            deleteGroup.mutate(g.id, {
                              onError: fail("Delete group"),
                            })
                          }
                        >
                          <DeleteIcon sx={{ fontSize: 15 }} />
                        </IconButton>
                      </>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Paper>
      )}

      {/* —— Invites —— */}
      {tab === 2 && (
        <Paper variant="outlined" sx={{ p: 2, maxWidth: 560 }}>
          <Typography variant="subtitle1" sx={{ mb: 1 }}>
            Borrower invite
          </Typography>
          {!isAdmin && (
            <Alert severity="info" variant="outlined" sx={{ mb: 1 }}>
              Invites are TenantAdmin-only.
            </Alert>
          )}
          <Box sx={{ display: "flex", gap: 1 }}>
            <TextField
              label="Borrower email"
              size="small"
              fullWidth
              value={inviteEmail}
              onChange={(e) => setInviteEmail(e.target.value)}
            />
            <Button
              variant="contained"
              disabled={!isAdmin || !inviteEmail.trim() || createInvite.isPending}
              onClick={doInvite}
            >
              Invite
            </Button>
          </Box>
          {inviteResult && (
            <Alert severity="success" variant="outlined" sx={{ mt: 2 }} onClose={() => setInviteResult(null)}>
              Invite created — share this link (shown once, expires{" "}
              {new Date(inviteResult.expiresUtc).toLocaleString()}):
              <Typography
                variant="body2"
                sx={{ fontFamily: "monospace", wordBreak: "break-all", mt: 0.5 }}
              >
                {inviteResult.inviteUrl}
              </Typography>
            </Alert>
          )}
        </Paper>
      )}

      {/* —— Tenant audit —— */}
      {tab === 3 && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Box sx={{ display: "flex", gap: 1, mb: 1.5, flexWrap: "wrap" }}>
            <TextField
              label="Actor"
              size="small"
              value={auditFilters.actor}
              onChange={(e) => setAuditFilters({ ...auditFilters, actor: e.target.value })}
            />
            <TextField
              label="Action"
              size="small"
              value={auditFilters.action}
              onChange={(e) => setAuditFilters({ ...auditFilters, action: e.target.value })}
            />
            <TextField
              label="Since"
              size="small"
              type="datetime-local"
              value={auditFilters.since}
              onChange={(e) => setAuditFilters({ ...auditFilters, since: e.target.value })}
              slotProps={{ inputLabel: { shrink: true } }}
            />
          </Box>
          {audit.isLoading && <Skeleton variant="rectangular" height={160} />}
          {audit.isError && <ErrorPanel title="Audit failed" error={audit.error} />}
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell sx={{ pl: 0 }}>When</TableCell>
                <TableCell>Actor</TableCell>
                <TableCell>Action</TableCell>
                <TableCell>Entity</TableCell>
                <TableCell>Fields</TableCell>
                <TableCell>Reason</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {(audit.data ?? []).map((e) => (
                <TableRow key={e.eventId}>
                  <TableCell sx={{ pl: 0, whiteSpace: "nowrap" }}>
                    {e.createdUtc ? new Date(e.createdUtc).toLocaleString() : "—"}
                  </TableCell>
                  <TableCell>{e.actor}</TableCell>
                  <TableCell>{e.action}</TableCell>
                  <TableCell>
                    {e.entity}
                    {e.entityId ? `/${e.entityId.slice(0, 8)}` : ""}
                  </TableCell>
                  <TableCell>{e.fieldClasses.join(", ") || "—"}</TableCell>
                  <TableCell>{e.reason ?? "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Paper>
      )}

      {/* —— Retention —— */}
      {tab === 4 && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Typography variant="subtitle1" sx={{ mb: 1 }}>
            Retention report
          </Typography>
          {retention.isLoading && <Skeleton variant="rectangular" height={80} />}
          {retention.isError && <ErrorPanel title="Retention failed" error={retention.error} />}
          {retention.isSuccess && (
            <>
              <Alert severity="info" variant="outlined" sx={{ mb: 1.5 }}>
                Earliest eligible clear:{" "}
                <strong>
                  {retention.data.earliestClearUtc
                    ? new Date(retention.data.earliestClearUtc).toLocaleString()
                    : "nothing yet eligible"}
                </strong>{" "}
                — Reg B/ECOA 25-month windows are enforced before erasure.
              </Alert>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ pl: 0 }}>Application</TableCell>
                    <TableCell>Created</TableCell>
                    <TableCell>Retain until</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {retention.data.applications.map((r) => (
                    <TableRow key={r.appId}>
                      <TableCell sx={{ pl: 0, fontFamily: "monospace" }}>{r.appId}</TableCell>
                      <TableCell>{new Date(r.createdUtc).toLocaleDateString()}</TableCell>
                      <TableCell>{new Date(r.retentionUntilUtc).toLocaleDateString()}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </>
          )}
        </Paper>
      )}

      {/* Create-user dialog */}
      <Dialog open={userDialog} onClose={() => setUserDialog(false)} maxWidth="xs" fullWidth>
        <DialogTitle>Create user</DialogTitle>
        <DialogContent>
          <TextField
            label="Email"
            size="small"
            fullWidth
            sx={{ mt: 1 }}
            value={userForm.email}
            onChange={(e) => setUserForm({ ...userForm, email: e.target.value })}
          />
          <TextField
            label="Display name"
            size="small"
            fullWidth
            sx={{ mt: 2 }}
            value={userForm.displayName}
            onChange={(e) => setUserForm({ ...userForm, displayName: e.target.value })}
          />
          <TextField
            label="Initial password (optional)"
            size="small"
            type="password"
            fullWidth
            sx={{ mt: 2 }}
            value={userForm.password}
            onChange={(e) => setUserForm({ ...userForm, password: e.target.value })}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setUserDialog(false)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={!userForm.email.trim() || createUser.isPending}
            onClick={doCreateUser}
          >
            Create
          </Button>
        </DialogActions>
      </Dialog>

      {/* Group create/edit dialog */}
      <Dialog open={groupDialog} onClose={() => setGroupDialog(false)} maxWidth="xs" fullWidth>
        <DialogTitle>{editGroup ? `Edit ${editGroup.name}` : "Create group"}</DialogTitle>
        <DialogContent>
          <TextField
            label="Name"
            size="small"
            fullWidth
            sx={{ mt: 1 }}
            value={groupForm.name}
            onChange={(e) => setGroupForm({ ...groupForm, name: e.target.value })}
          />
          <TextField
            label="Description"
            size="small"
            fullWidth
            sx={{ mt: 2 }}
            value={groupForm.description}
            onChange={(e) => setGroupForm({ ...groupForm, description: e.target.value })}
          />
          <TextField
            select
            label="Role granted to members"
            size="small"
            fullWidth
            sx={{ mt: 2 }}
            value={groupForm.roles[0] ?? ""}
            onChange={(e) =>
              setGroupForm({ ...groupForm, roles: e.target.value ? [e.target.value] : [] })
            }
          >
            <MenuItem value="">None</MenuItem>
            {MEMBER_ROLE_CHOICES.map((r) => (
              <MenuItem key={r} value={r}>
                {r}
              </MenuItem>
            ))}
          </TextField>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setGroupDialog(false)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={!groupForm.name.trim() || createGroup.isPending || updateGroup.isPending}
            onClick={doSaveGroup}
          >
            Save
          </Button>
        </DialogActions>
      </Dialog>

      {/* Membership dialog */}
      <Dialog open={!!membersFor} onClose={() => setMembersFor(null)} maxWidth="sm" fullWidth>
        <DialogTitle>Members — {membersFor?.name}</DialogTitle>
        <DialogContent>
          {members.isLoading && <Skeleton variant="rectangular" height={60} />}
          <Table size="small">
            <TableBody>
              {(members.data ?? []).map((uid) => {
                const u = users.data?.find((x) => x.id === uid);
                return (
                  <TableRow key={uid}>
                    <TableCell sx={{ pl: 0 }}>
                      {u ? `${u.displayName} (${u.email})` : uid}
                    </TableCell>
                    <TableCell align="right">
                      {isAdmin && (
                        <IconButton
                          size="small"
                          aria-label={`remove ${u?.displayName ?? uid}`}
                          onClick={() =>
                            removeMember.mutate(
                              { gid: membersFor!.id, userId: uid },
                              { onError: fail("Remove member") },
                            )
                          }
                        >
                          <PersonRemoveIcon sx={{ fontSize: 15 }} />
                        </IconButton>
                      )}
                    </TableCell>
                  </TableRow>
                );
              })}
              {members.isSuccess && members.data!.length === 0 && (
                <TableRow>
                  <TableCell sx={{ pl: 0, border: 0 }}>
                    <Typography color="text.secondary" variant="body2">
                      No members.
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
          {isAdmin && (
            <Box sx={{ display: "flex", gap: 1, mt: 2 }}>
              <TextField
                select
                label="Add member"
                size="small"
                fullWidth
                value={memberPick}
                onChange={(e) => setMemberPick(e.target.value)}
              >
                {(users.data ?? [])
                  .filter((u) => !(members.data ?? []).includes(u.id))
                  .map((u) => (
                    <MenuItem key={u.id} value={u.id}>
                      {u.displayName} ({u.email})
                    </MenuItem>
                  ))}
              </TextField>
              <Button
                variant="outlined"
                disabled={!memberPick || addMember.isPending}
                onClick={() =>
                  addMember.mutate(
                    { gid: membersFor!.id, userId: memberPick },
                    {
                      onSuccess: (r) => {
                        if (r.status < 400) setMemberPick("");
                        else setPageError(`Add member failed: HTTP ${r.status}`);
                      },
                      onError: fail("Add member"),
                    },
                  )
                }
              >
                Add
              </Button>
            </Box>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setMembersFor(null)}>Close</Button>
        </DialogActions>
      </Dialog>
    </>
  );
}
