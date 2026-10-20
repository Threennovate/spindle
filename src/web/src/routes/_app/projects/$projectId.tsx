import { createFileRoute } from '@tanstack/react-router'
import { ProjectDetailPage } from '@/features/projects/project-detail-page'

export const Route = createFileRoute('/_app/projects/$projectId')({
  component: ProjectRoute,
})

function ProjectRoute() {
  const { projectId } = Route.useParams()
  return <ProjectDetailPage key={projectId} projectId={projectId} />
}
